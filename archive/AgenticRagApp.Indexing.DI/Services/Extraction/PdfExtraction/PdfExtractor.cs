using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AgenticRagApp.Indexing.DI.Models;
using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Indexing.DI.Services;

// Azure Document Intelligence ("prebuilt-layout") PDF extraction backend, ported
// from the comparison spike's DocumentIntelligenceExtractionService, with chunking
// stripped out entirely (chunking stays downstream, in ChunkingService, unchanged).
// Owns the PdfDocument's lifetime up front: preflight (PdfDocumentValidator.IsPDFValid)
// opens and validates it, then PdfNativeMetadataExtractor.ExtractPdfNativeMetadataAndDispose takes
// over that lifetime - it reads native metadata/bookmarks off the PdfDocument and disposes it
// before returning, so nothing here needs its own `using` block. The resulting
// DocMetadata is handed to PdfDocumentIntelligenceAnalyzer.AnalyzeDocumentAsync, which does
// the paid call, markdown page assembly, and structural extraction from there.
// This class is the assembler: it combines what each of the three steps produced into
// one PdfExtractionResult - the complete record of everything the pipeline learned about
// this PDF - rather than each step's output getting scattered/lost along the way.
public class PdfExtractor : IPdfExtractor
{
    public string Name => "DocumentIntelligence";

    private readonly ILogger<PdfExtractor> _logger;
    private readonly PdfDocumentIntelligenceAnalyzer                      _pdfDocumentIntelligenceAnalyzer;

    public PdfExtractor(PdfDocumentIntelligenceAnalyzer structureExtractor, ILogger<PdfExtractor>? logger = null)
    {
        _logger             = logger ?? NullLogger<PdfExtractor>.Instance;
        _pdfDocumentIntelligenceAnalyzer = structureExtractor;
    }

    public async Task<PdfExtractionResult> ExtractPDFAsync(string blobName, byte[] pdfBytes, CancellationToken ct = default)
    {
        var fileSizeBytes = pdfBytes.LongLength;

        // Step 1: local, free structural check — rejects oversized/corrupt/encrypted/
        // too-many-page files before spending a paid Document Intelligence call on them.
        if (!PdfDocumentValidator.IsPDFValid(pdfBytes, blobName, _logger, out var pdf, out var validationError, out var validationDiagnostics))
            return new PdfExtractionResult(false, blobName, fileSizeBytes, null, null, null, null, null, null, validationError)
            {
                ValidationDiagnostics = validationDiagnostics,
            };

        // Captured before Step 2 disposes pdf - PdfPig's own PDF spec version (e.g. 1.7),
        // otherwise only ever logged and then lost. Technically outside any try/finally
        // (Step 2's `using (pdf)` doesn't start until the next line), so a throw here would
        // leak pdf - accepted, since Version is a stored-value property read, not a
        // realistic throw site.
        var pdfSpecVersion = (double?)pdf.Version;

        // Step 2: ParseNativeMetadata takes ownership of pdf's lifetime (disposes it internally)
        // and reads everything PdfPig can offer beyond DI: native Title/Author/
        // CreationDate plus the outline/bookmark tree.
        var nativeMetadata = PdfNativeMetadataExtractor.ExtractPdfNativeMetadataAndDispose(pdf, blobName, _logger, out var metadataDiagnostics);

        // Step 3: submit to Document Intelligence's prebuilt-layout model and assemble
        // pages/structural data — lives in PdfDocumentIntelligenceAnalyzer.
        var documentAnalyzed = await _pdfDocumentIntelligenceAnalyzer.AnalyzeDocumentAsync(pdfBytes, blobName, nativeMetadata, ct);
        if (!documentAnalyzed.Ok)
            return new PdfExtractionResult(false, blobName, fileSizeBytes, pdfSpecVersion, nativeMetadata, null, null, null, null, documentAnalyzed.Error)
            {
                ValidationDiagnostics = validationDiagnostics,
                MetadataDiagnostics   = metadataDiagnostics,
                DocumentIntelligenceDiagnostics   = new PdfStepDiagnostics([], [documentAnalyzed.Error!]),
            };
        
        // Bookmarks are PdfPig-derived (NativeMetadata), not DI-derived, so this is
        // computed here rather than inside PdfDocumentIntelligenceAnalyzer/PdfDocumentStructure,
        // which is scoped to what DI itself produces.
        var (sectionBreadcrumbs, breadcrumbDiagnostics) =
            PdfSectionBreadCrumbBuilder.BuildSectionBreadcrumbs(nativeMetadata.Bookmarks, nativeMetadata.PageCount, blobName);

        return new PdfExtractionResult(
            Ok:               true,
            BlobName:         blobName,
            FileSizeBytes:    fileSizeBytes,
            PdfSpecVersion:   pdfSpecVersion,
            NativeMetadata:   nativeMetadata,
            RawContent:       documentAnalyzed.RawContent,
            Pages:            documentAnalyzed.Pages,
            Structure:        documentAnalyzed.Structure,
            EstimatedCostUsd: documentAnalyzed.EstimatedCostUsd,
            Error:            null)
        {
            // Without this, every AnalysisWarning PdfDocumentIntelligenceAnalyzer produces (DI's own
            // top-level warnings, the non-BMP character check) would reach this far and
            // then be silently dropped instead of flowing into the same
            // PdfPipelineValidator -> PdfQualityGateResult.Issues path every other
            // extraction warning already uses.
            Warnings = documentAnalyzed.Warnings.Select(w => ToPipelineIssue(w, blobName)).ToList(),

            SectionBreadcrumbs = sectionBreadcrumbs,

            // Per-step diagnostics (see PdfStepDiagnostics) - report/diagnostic material
            // only, never a second gating source. AnalysisDiagnostics.Warnings is the same
            // mapped list as the flat Warnings above by design - one signal, two views
            // ("everything this file produced" vs. "grouped by which step produced it").
            ValidationDiagnostics = validationDiagnostics,
            MetadataDiagnostics   = metadataDiagnostics,
            // Infos (e.g. CostInfo's "estimated cost: $X (N pages)") previously computed
            // and discarded - PdfExtractionResult had no field to receive them. Mapped here
            // the same way Warnings already are, so they reach validation-report.json
            // instead of being dead weight (finding #10).
            DocumentIntelligenceDiagnostics   = new PdfStepDiagnostics(
                documentAnalyzed.Warnings.Select(w => ToPipelineIssue(w, blobName)).ToList(),
                [],
                Info: documentAnalyzed.Infos.Select(w => ToPipelineIssue(w, blobName)).ToList()),
            BreadcrumbDiagnostics = breadcrumbDiagnostics,

            // Pages/Structure are guaranteed non-null here (documentAnalyzed.Ok already
            // returned true above), but computed defensively via ?? [] rather than a
            // null-forgiving operator - a routing computation is not the place to newly
            // introduce a throw on a shape another gate was supposed to have already caught.
            Profile = DocumentProfileHelper.Compute(
                documentAnalyzed.Pages ?? [],
                documentAnalyzed.Structure?.Figures ?? [],
                fileSizeBytes,
                documentAnalyzed.Structure?.Headings ?? [],
                documentAnalyzed.Structure?.Boilerplate ?? [],
                documentAnalyzed.Structure?.SelectionMarks ?? [],
                documentAnalyzed.RawContent?.Length),

            Language = documentAnalyzed.Language,
        };
    }

    // Document Intelligence's own analysis warnings, rebadged as pipeline issues. Stage is
    // ParsePages because these come from the analyze call that produces the page records -
    // not from the local preflight gates (Validation) or the PdfPig reads (Metadata).
    private static PipelineIssue ToPipelineIssue(AnalysisWarning warning, string blobName) =>
        PipelineIssue.Warning(
            PipelineStage.ParsePages,
            blobName,
            string.IsNullOrEmpty(warning.Code) ? warning.Message ?? "" : $"[{warning.Code}] {warning.Message}");
}
