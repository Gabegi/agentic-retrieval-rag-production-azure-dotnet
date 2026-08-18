using AgenticRagApp.Common.Models;
using AgenticRagApp.Indexing.Pdf.Utils;

namespace AgenticRagApp.Indexing.Pdf.Models;

// One whole PDF handed to the chunking pipeline - not one page (action-plan.md §3.1, C8).
//
// This used to be a per-PAGE record, and every consumer that needed document semantics
// undid that split itself: DocumentIdentityResolver grouped by SourceId to gather headings,
// ExtractionService counted distinct SourceIds to get a document count, ChunkingService
// ordered by SourceId then Ordinal to rebuild reading order. Three regroups, each with a
// comment apologising for the shape. Meanwhile Document Intelligence analyses a whole
// document in the first place - AnalyzeResult.Content IS the document - so the pages were
// a slice made only to be glued back together.
//
// It also cost: the whole file's Sections list was attached to every page, so serialized
// size grew with sections x pages. Carrying file-level data once removes that by
// construction.
//
// Per-page cleaning is unaffected. PdfCleaner still cleans page by page, and one bad page
// still becomes a PipelineIssue rather than failing the file; extraction assembles the
// cleaned pages afterwards and records where each one landed (PageSpans). Per-page error
// isolation and a single coordinate system were never actually in tension - only the
// output record's shape coupled them.
//
// Deliberately NOT deriving from ExtractionDocumentBase any more: that base is
// (SourceId, Ordinal, Content), and Ordinal was the page number. A document has no
// ordinal, and inheriting one that means nothing is worse than not sharing a base at all.
// CSV keeps its own row-shaped record.
public sealed record PdfExtractionDocument(
    // Grouping/chunking boundary - blobName. The chunker never blends across SourceIds.
    string SourceId,

    // The whole document's cleaned text, assembled from its pages in page order.
    // PageSpans says which range came from which page.
    string Content,

    // Where each page's text sits in Content, in page order. Recorded during assembly, so
    // exact rather than reconstructed - see PageSpan.
    IReadOnlyList<PageSpan> PageSpans,

    // ── File-level facts (carried once, not repeated per page) ──────────────

    // Native PDF Title if the file has one, else a filename-derived fallback.
    string Title,

    // Native PDF Info-dictionary facts (PdfNativeMetadataExtractor). ModDate is when the
    // content was actually last edited - the real "is this policy current" signal, distinct
    // from LastModifiedDate (blob re-upload timing).
    string?         Author,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? ModDate,
    int?            PageCount,
    DateTimeOffset? LastModifiedDate,

    // Zenya's own identity/lifecycle facts, from custom blob metadata rather than the PDF
    // itself. All null is the expected default until whoever uploads a PDF sets it - a real
    // traceability gap for chunks built from this document, not a bug.
    string? ZenyaDocumentId,
    string? ZenyaVersion,
    string? ZenyaStatus,
    string? ZenyaUrl,

    // Raw bookmark/outline tree. Only 5 of 51 documents have one, and the four largest have
    // none - which is why DI's detected headings, not this, are the primary boundary signal.
    IReadOnlyList<Bookmark> Bookmarks,

    // Page number -> breadcrumb text, where the outline covers that page. Kept as a map
    // rather than resolved onto pages, since a chunk can now span pages.
    IReadOnlyDictionary<int, string> PageBreadcrumbs,

    // DI's own semantic section tree. Phase A measured its boundaries as identical to the
    // DI headings below (99.4-100%, both directions), so it is a hierarchy cross-check
    // rather than a second boundary source - its spans nest, which the flat heading list
    // does not express.
    IReadOnlyList<SectionInfo> Sections,

    // ── Document-scoped structure (every element carries its own PageNumber) ─
    // No longer filtered per page: page filtering existed only to keep the per-page record
    // from carrying the whole file's structure, and there is no per-page record now.

    IReadOnlyList<Heading>           Headings,
    IReadOnlyList<Heading>           Boilerplate,
    IReadOnlyList<TableInfo>         Tables,
    IReadOnlyList<SelectionMarkInfo> SelectionMarks,
    IReadOnlyList<FigureInfo>        Figures,
    IReadOnlyList<LineInfo>          Lines,

    // ── Profile measurements (action-plan.md C7) ────────────────────────────

    // Computed at extraction and, until now, read by nothing at all. Carries the measured
    // inputs to all three first-split decisions (chars/page and bytes/char for the
    // extraction gate, EstimatedTokens for the parent grain, heading counts for the
    // navigation grain). The old Route enum is gone - it fused a density test and a token
    // tier into one four-way value that could not express "large but unstructured".
    DocumentProfile? Profile,

    // "nl"/"en" from DI's own AnalyzeResult.Languages. The corpus is Dutch plus one
    // 36-page English document whose chars/token ratio is ~4 rather than ~3.2, which makes
    // every character-derived ceiling wrong for it - including its own routing input.
    string? Language,

    // ── Resolved at chunking time, not at extraction ────────────────────────

    // FamilyId, DomainTag and ConfusableWith, from DocumentIdentityResolver (chunking step 1).
    // Extraction never sets this - it is null on the extraction artifact and attached with
    // `doc with { Family = ... }` once identity resolution has run.
    //
    // It rides on the document rather than being threaded as a parameter because two different
    // steps need it and neither should have to be handed it separately: the strategy prices
    // DomainTag INTO the embedded prefix ("title [tag]") before the cut, and step 4 stamps all
    // three onto every chunk of the document.
    DocumentFamily? Family = null,

    // The document's heading sections, anchored in CLEANED coordinates by HeadingLocator and
    // attached the same way Family is - `doc with { LocatedSections = ... }` - before the
    // strategy runs.
    //
    // Located in ChunkingService rather than inside the strategy so the run report can see the
    // three heading counters even for a document that goes on to produce no chunks at all. That
    // is the case the >2% escalation threshold exists to catch, and a strategy that returns only
    // chunks cannot report it.
    //
    // Null on the recursive route, which never anchors. Null means NOT ATTEMPTED, never "every
    // heading failed to locate" - the same distinction the zero counters carry.
    IReadOnlyList<LocatedSection>? LocatedSections = null);
