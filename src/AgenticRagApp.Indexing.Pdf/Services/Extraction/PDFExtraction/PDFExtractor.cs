using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Azure Document Intelligence ("prebuilt-layout") PDF extraction backend, ported
// from the comparison spike's DocumentIntelligenceExtractionService, with chunking
// stripped out entirely (chunking stays downstream, in ChunkingService, unchanged).
// Owns the PdfDocument's lifetime up front: preflight (PdfDocumentValidator.IsPDFValid)
// opens and validates it, then PdfNativeMetadataExtractor.ExtractPdfNativeMetadataAndDispose takes
// over that lifetime - it reads native metadata/bookmarks off the PdfDocument and disposes it
// before returning, so nothing here needs its own `using` block. The resulting
// DocMetadata is handed to PdfDocumentAnalyzer.AnalyzeDocumentAsync, which does
// the paid call, markdown page assembly, and structural extraction from there.
// This class is the assembler: it combines what each of the three steps produced into
// one PDFExtractionResult - the complete record of everything the pipeline learned about
// this PDF - rather than each step's output getting scattered/lost along the way.
public class PDFExtractor : IPdfExtractor
{
    public string Name => "DocumentIntelligence";

    private readonly ILogger<PDFExtractor> _logger;
    private readonly PdfDocumentIntelligenceAnalyzer                      _pdfDocumentIntelligenceAnalyzer;

    public PDFExtractor(PdfDocumentIntelligenceAnalyzer structureExtractor, ILogger<PDFExtractor>? logger = null)
    {
        _logger             = logger ?? NullLogger<PDFExtractor>.Instance;
        _pdfDocumentIntelligenceAnalyzer = structureExtractor;
    }

    public async Task<PDFExtractionResult> ExtractPDFAsync(string blobName, byte[] pdfBytes, CancellationToken ct = default)
    {
        var fileSizeBytes = pdfBytes.LongLength;

        // Step 1: local, free structural check — rejects oversized/corrupt/encrypted/
        // too-many-page files before spending a paid Document Intelligence call on them.
        if (!PdfDocumentValidator.IsPDFValid(pdfBytes, blobName, _logger, out var pdf, out var validationError, out var validationDiagnostics))
            return new PDFExtractionResult(false, blobName, fileSizeBytes, null, null, null, null, null, null, validationError)
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
        // pages/structural data — lives in PdfDocumentAnalyzer.
        var documentAnalyzed = await _pdfDocumentIntelligenceAnalyzer.AnalyzeDocumentAsync(pdfBytes, blobName, nativeMetadata, ct);
        if (!documentAnalyzed.Ok)
            return new PDFExtractionResult(false, blobName, fileSizeBytes, pdfSpecVersion, nativeMetadata, null, null, null, null, documentAnalyzed.Error)
            {
                ValidationDiagnostics = validationDiagnostics,
                MetadataDiagnostics   = metadataDiagnostics,
                DocumentIntelligenceDiagnostics   = new PdfStepDiagnostics([], [documentAnalyzed.Error!]),
            };
        
        // Bookmarks are PdfPig-derived (NativeMetadata), not DI-derived, so this is
        // computed here rather than inside PdfDocumentAnalyzer/PdfDocumentStructure,
        // which is scoped to what DI itself produces.
        var (sectionBreadcrumbs, breadcrumbDiagnostics) =
            PdfSectionBreadCrumbBuilder.BuildSectionBreadcrumbs(nativeMetadata.Bookmarks, nativeMetadata.PageCount, blobName);

        return new PDFExtractionResult(
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
            // Without this, every AnalysisWarning PdfDocumentAnalyzer produces (DI's own
            // top-level warnings, the non-BMP character check) would reach this far and
            // then be silently dropped instead of flowing into the same
            // PdfPipelineValidator -> PdfValidationReport.Issues path every other
            // extraction warning already uses.
            Warnings = documentAnalyzed.Warnings.Select(w => ToExtractionWarning(w, blobName)).ToList(),

            SectionBreadcrumbs = sectionBreadcrumbs,

            // Per-step diagnostics (see PdfStepDiagnostics) - report/diagnostic material
            // only, never a second gating source. AnalysisDiagnostics.Warnings is the same
            // mapped list as the flat Warnings above by design - one signal, two views
            // ("everything this file produced" vs. "grouped by which step produced it").
            ValidationDiagnostics = validationDiagnostics,
            MetadataDiagnostics   = metadataDiagnostics,
            DocumentIntelligenceDiagnostics   = new PdfStepDiagnostics(documentAnalyzed.Warnings.Select(w => ToExtractionWarning(w, blobName)).ToList(), []),
            BreadcrumbDiagnostics = breadcrumbDiagnostics,
        };
    }

    private static ExtractionWarning ToExtractionWarning(AnalysisWarning warning, string blobName) => new(
        RowNumber:  null,
        DocumentId: blobName,
        Message:    string.IsNullOrEmpty(warning.Code) ? warning.Message ?? "" : $"[{warning.Code}] {warning.Message}");
}
