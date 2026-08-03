using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Indexing.Pdf.Models;

// One PDF page handed to the chunking pipeline. Fully typed on purpose - PDF is this
// project's only source (see docs/plan210726.md's "no generic" note), and a Metadata
// string dictionary is exactly what let six CSV-era fields (folder_path, quick_code,
// check_date, version, summary, plus a Heading nobody ever set) sit unused for so long
// without anyone noticing: nothing forced a reader to account for every key. A typed
// record forces that - every field here is either read by ChunkingService or visibly
// unused right here, not buried behind a string key.
//
// Every field extraction produces that's actually useful downstream is carried through -
// not silently dropped, even where nothing consumes a given field yet (see
// docs/chunking-rewrite-plan.md). File-level facts (native metadata, bookmarks, DI
// sections) are duplicated identically across every page of one file, same as Title
// already was before this rewrite. Page-level structure (headings, tables, figures,
// lines, selection marks, boilerplate, dimensions, OCR confidence) is filtered to just
// this page's PageNumber - not the whole file's Structure repeated per page, which would
// blow up an N-page document's payload roughly N-fold for no reason.
//
// Deliberately NOT carried:
//   - PdfExtractionResult.FileSizeBytes/PdfSpecVersion/EstimatedCostUsd - operational
//     facts already surfaced in the extraction run report, not something a chunk needs.
//   - PdfExtractionResult.PageErrors/Warnings - not dropped data, already fully surfaced
//     through PdfPipelineValidator -> PdfExtractionOutput.Issues -> the run report.
//     Duplicating either here would repackage identical data, not add anything new.
//   - DocMetadata.Producer/Creator/Subject/Keywords - diagnostics-only signals (pipeline
//     provenance/QA, e.g. flagging a PDF with no Producer as a non-standard export path -
//     see PdfNativeMetadataExtractor's Producer-missing warning). Not chunk-worthy, same
//     reasoning as FileSizeBytes/PdfSpecVersion above.
public sealed record PdfExtractionDocument(
    string SourceId,   // grouping/chunking boundary — the chunker never blends chunks across different SourceIds; blobName for PDF
    int    Ordinal,    // page number — used for ordering only

    string Content,

    // ── File-level (same value on every page of one file) ──────────────────

    // Native PDF Title if the file has one set, else a filename-derived fallback -
    // see PdfDocumentIntelligenceAnalyzer.GetTitle.
    string Title,

    // Native PDF Info-dictionary facts (PdfNativeMetadataExtractor).
    string?         Author,
    DateTimeOffset? CreatedAt,
    // The PDF's own ModDate - when the file's content was actually last edited, distinct
    // from LastModifiedDate below (which only reflects blob re-upload timing, not content
    // changes). The real "is this policy current" signal for citations.
    DateTimeOffset? ModDate,
    int?            PageCount,

    // The blob's own storage LastModified, full precision (not date-truncated - that
    // truncation only ever mattered for ExtractionService's own diff comparison, which
    // reads the blob property directly, not this field).
    DateTimeOffset? LastModifiedDate,

    // Zenya's own identity/lifecycle facts (ZenyaMetadata.FromBlobMetadata) - sourced from
    // custom blob metadata, not the PDF itself (see ZenyaMetadata's comment for why). All
    // null is the expected default until whoever uploads a PDF starts setting this metadata;
    // that's a real traceability gap for a chunk built from this document, not a bug.
    string? ZenyaDocumentId,
    string? ZenyaVersion,
    string? ZenyaStatus,
    string? ZenyaUrl,

    // Raw bookmark/outline tree (Breadcrumb below is the resolved per-page projection of
    // this - kept here too since the tree itself, e.g. full depth/structure, is lossy to
    // collapse into a single breadcrumb string).
    IReadOnlyList<Bookmark> Bookmarks,

    // DI's own semantic section boundaries - not page-scoped (a section's Elements are
    // JSON-pointer refs that can span pages), so carried at file level, same on every page.
    // Not consumed by chunk *splitting* yet - that's a chunking-strategy decision, reviewed
    // separately - but the data isn't dropped in the meantime.
    IReadOnlyList<SectionInfo> Sections,

    // ── Page-level (filtered to this page's PageNumber) ─────────────────────

    // Resolved section context for this page, when the PDF has an outline - hierarchical,
    // e.g. "Chapter 3 > 3.2 Dosage" (PDFSectionBreadCrumbBuilder). Null means no outline
    // covers this page, not "unknown."
    string? Breadcrumb,

    // DI-detected heading paragraphs (title/sectionHeading roles) on this page - works even
    // when the PDF has no bookmark outline at all, unlike Breadcrumb above.
    IReadOnlyList<Heading> Headings,

    // DI-detected boilerplate paragraphs (pageHeader/pageFooter/footnote/pageNumber roles)
    // on this page. Not stripped from Content today - see PdfCleaner's own comment on why
    // header/footer stripping is deliberately deferred - but available here for whichever
    // step picks that up.
    IReadOnlyList<Heading> Boilerplate,

    IReadOnlyList<TableInfo> Tables,

    // Physical page geometry - for a future highlight-on-source feature (pairs with Lines'
    // polygons), not used by embedding/retrieval today.
    PageDimensions? Dimensions,

    IReadOnlyList<SelectionMarkInfo> SelectionMarks,
    IReadOnlyList<FigureInfo>        Figures,
    IReadOnlyList<LineInfo>          Lines
) : ExtractionDocumentBase(SourceId, Ordinal, Content);
