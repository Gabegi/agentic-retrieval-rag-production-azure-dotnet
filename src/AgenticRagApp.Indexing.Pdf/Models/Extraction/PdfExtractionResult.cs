using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Indexing.Pdf.Models;

// One PDF file's complete extraction outcome - everything every step of the pipeline
// (PdfDocumentValidator, PdfNativeMetadataExtractor, PdfDocumentIntelligenceAnalyzer) produced for
// this file, combined into one object by DocumentIntelligenceExtractor. Error is set (and
// every data field null) when the file couldn't be parsed at all (corrupt PDF, backend
// exception) — the orchestrator folds this into the same ExtractionBatch<T>.Errors
// bucket CSV's row-level parse errors land in.
//
// Explicit properties + constructor (not positional record syntax) so the Ok/Error
// invariant can be enforced at construction: Ok and Error can't diverge, so callers of
// either f.Ok or f.Error != null are provably checking the same thing, rather than
// relying on every construction site happening to keep them in lockstep by convention.
public record PdfExtractionResult
{
    // True on success, false on failure - every field below except BlobName/FileSizeBytes/
    // Error is null when Ok is false.
    public bool   Ok       { get; }
    public string BlobName { get; }

    // Step 1: PdfDocumentValidator - free facts, always known once the file has been read
    // off blob storage, independent of whether it goes on to open/analyze successfully.
    public long    FileSizeBytes  { get; }
    public double? PdfSpecVersion { get; } // e.g. 1.7 - PdfPig's opened.Version; null unless the file fully passed validation

    // Step 2: PdfNativeMetadataExtractor - PdfPig-native, independent of DI. Null only
    // when the file failed before/during opening.
    public DocMetadata? NativeMetadata { get; }

    // Step 3: PdfDocumentIntelligenceAnalyzer - the paid Document Intelligence call.
    public string? RawContent { get; } // analysis.Content, unsplit, before per-page assembly

    // Raw extractor output - mojibake, blank-line noise, etc. not yet repaired. Never
    // consume this for content; go through PdfCleanResult (PdfPipelineValidator.Aggregate
    // -> PdfCleaner.Clean) instead, so the extract-vs-clean reconciliation check stays a
    // real comparison of two independent states, not the same data read twice.
    public IReadOnlyList<PdfPageRecord>? Pages { get; }
    public PdfDocumentStructure?         Structure { get; }
    public decimal?                      EstimatedCostUsd { get; }

    public PipelineIssue? Error { get; }

    // Parameter names match the property names exactly (kept capitalized, unlike normal
    // constructor convention) so every existing call site using named arguments
    // (Ok: ..., BlobName: ..., etc.) keeps compiling unchanged - this replaces positional
    // record syntax, not the calling convention callers already use.
    public PdfExtractionResult(
        bool Ok, string BlobName, long FileSizeBytes, double? PdfSpecVersion,
        DocMetadata? NativeMetadata, string? RawContent, IReadOnlyList<PdfPageRecord>? Pages,
        PdfDocumentStructure? Structure, decimal? EstimatedCostUsd, PipelineIssue? Error)
    {
        if (Ok != (Error is null))
            throw new ArgumentException(
                $"PdfExtractionResult for '{BlobName}': Ok={Ok} but Error={(Error is null ? "null" : "set")} - they must agree.");
        if (Ok && Pages is null)
            throw new ArgumentException($"PdfExtractionResult for '{BlobName}': Ok=true but Pages is null.");

        this.Ok               = Ok;
        this.BlobName         = BlobName;
        this.FileSizeBytes    = FileSizeBytes;
        this.PdfSpecVersion   = PdfSpecVersion;
        this.NativeMetadata   = NativeMetadata;
        this.RawContent       = RawContent;
        this.Pages            = Pages;
        this.Structure        = Structure;
        this.EstimatedCostUsd = EstimatedCostUsd;
        this.Error            = Error;
    }

    // Per-page failures/soft-quality signals that don't fail the whole file (e.g. one
    // unreadable page, a likely-scanned page). Folded into the aggregate ExtractionBatch
    // by PdfPipelineValidator, same bucket a file-level Error would land in.
    public IReadOnlyList<PipelineIssue>   PageErrors  { get; init; } = [];
    public IReadOnlyList<PipelineIssue> Warnings    { get; init; } = [];

    // Per-step diagnostics (see PdfStepDiagnostics) - report/diagnostic material only,
    // never fed into validation gating. Lets a report say which step a finding came
    // from, instead of everything landing in the one undifferentiated Warnings/
    // PageErrors pile above. ValidationDiagnostics is populated even when Ok is false
    // (it mirrors the file-level Error so "which step failed" is answerable the same
    // way for every step); the other three are empty when the pipeline never reached
    // that step.
    public PdfStepDiagnostics ValidationDiagnostics { get; init; } = PdfStepDiagnostics.Empty;
    public PdfStepDiagnostics MetadataDiagnostics    { get; init; } = PdfStepDiagnostics.Empty;
    public PdfStepDiagnostics DocumentIntelligenceDiagnostics    { get; init; } = PdfStepDiagnostics.Empty;
    public PdfStepDiagnostics BreadcrumbDiagnostics  { get; init; } = PdfStepDiagnostics.Empty;

    // Page number -> breadcrumb text (e.g. "Section: Chapter 3 > 3.2 Dosage"), built from
    // NativeMetadata.Bookmarks by PDFSectionBreadCrumbBuilder. Empty when the PDF has no
    // outline. Not consumed by anything yet - a future chunk-builder attaches the entry
    // for whichever page(s) a chunk falls on.
    public IReadOnlyDictionary<int, string> SectionBreadcrumbs { get; init; } = new Dictionary<int, string>();

    // Document-level chunking lane (docs/2608/260811/chunkRoutes.md), computed by
    // ChunkRoutingHelper from Pages/FileSizeBytes/Structure.Figures once extraction
    // succeeds. Null when Ok is false - there are no pages to route.
    public DocumentRouting? Routing { get; init; }

    // "nl"/"en", read off DI's own AnalyzeResult.Languages by LanguageDetectionHelper
    // (chunking-signals-map.md §3c #3) - not re-detected, DI already computes this on
    // every call. Null when Ok is false.
    public string? Language { get; init; }
}
