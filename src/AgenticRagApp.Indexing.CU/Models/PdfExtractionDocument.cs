using AgenticRagApp.Common.Models;
using AgenticRagApp.Indexing.CU.Utils;

namespace AgenticRagApp.Indexing.CU.Models;

// One whole PDF handed to the chunking pipeline - not one page (action-plan.md §3.1, C8).
//
// This used to be a per-PAGE record, and every consumer that needed document semantics
// undid that split itself: DocumentIdentityResolver grouped by SourceId to gather headings,
// ExtractionService counted distinct SourceIds to get a document count, ChunkingService
// ordered by SourceId then Ordinal to rebuild reading order. Three regroups, each with a
// comment apologising for the shape. Meanwhile the analyzer (DI then, Content Understanding now) analyses a whole
// document in the first place - its markdown IS the document - so the pages were
// a slice made only to be glued back together.
//
// It also cost: the whole file's Sections list was attached to every page, so serialized
// size grew with sections x pages. Carrying file-level data once removes that by
// construction.
//
// There is no per-page cleaning any more (PdfCleaner went with Document Intelligence): Content
// is the Content Understanding markdown VERBATIM, and PageSpans are the service page ranges
// into it - see PageSpan.
//
// Deliberately NOT deriving from ExtractionDocumentBase any more: that base is
// (SourceId, Ordinal, Content), and Ordinal was the page number. A document has no
// ordinal, and inheriting one that means nothing is worse than not sharing a base at all.
// CSV keeps its own row-shaped record.
public sealed record PdfExtractionDocument(
    // Grouping/chunking boundary - blobName. The chunker never blends across SourceIds.
    string SourceId,

    // The whole document as markdown, verbatim from Content Understanding. PageSpans says which
    // range the service attributes to which page.
    string Content,

    // Where each page's text sits in Content, in page order. Recorded during assembly, so
    // exact rather than reconstructed - see PageSpan.
    IReadOnlyList<PageSpan> PageSpans,

    // ── File-level facts (carried once, not repeated per page) ──────────────

    // The Role=Title paragraph Content Understanding classified, or "" when it classified none
    // (12 of 51 documents on the 260827 run). No fallback: the first section heading used to
    // stand in and produced "Inleiding" and "Inhoudsopgave" as titles (removed 2026-09-09).
    string Title,

    // Native PDF Info-dictionary facts (PdfNativeMetadataExtractor). ModDate is when the
    // content was actually last edited - the real "is this policy current" signal, distinct
    // from LastModifiedDate (blob re-upload timing).
    string?         Author,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? ModDate,
    int?            PageCount,
    DateTimeOffset? LastModifiedDate,

    // Page number -> breadcrumb text, where the outline covers that page. Kept as a map
    // rather than resolved onto pages, since a chunk can now span pages.
    IReadOnlyDictionary<int, string> PageBreadcrumbs,

    // The service section tree (CU DocumentContent.Sections). Phase A (DI-era) measured its
    // boundaries as identical to the headings below (99.4-100%, both directions), so it is a hierarchy cross-check
    // rather than a second boundary source - its spans nest, which the flat heading list
    // does not express.
    IReadOnlyList<SectionInfo> Sections,

    // ── Document-scoped structure (every element carries its own PageNumber) ─
    // No longer filtered per page: page filtering existed only to keep the per-page record
    // from carrying the whole file's structure, and there is no per-page record now.

    IReadOnlyList<Heading>           Headings,
    IReadOnlyList<Heading>           Boilerplate,
    IReadOnlyList<TableInfo>         Tables,
    IReadOnlyList<FigureInfo>        Figures,
    IReadOnlyList<AnnotationInfo>    Annotations,
    IReadOnlyList<HyperlinkInfo>     Hyperlinks,

    // DocumentProfile sat here until 2026-09-08, carrying the measured inputs to the
    // first-split routing decisions (action-plan.md C7). Deleted with those decisions: the
    // four-route design it fed is gone (D113 collapsed it to two strategies), 13 of its 16
    // fields had no reader, and nothing had produced one at all since the CU switch. The three
    // values that were still read are now measured where they are used - headings per 1,000
    // chars and the token count in ChunkingService's route gate, chars/page in the run report.

    // "nl"/"en" from IDocumentLanguageDetector (AI Language) since 2026-09-08; this used to say
    // "from DI's own AnalyzeResult.Languages", which stopped being true at the CU switch - CU
    // reports no detected language at all. The corpus is Dutch plus one
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
    IReadOnlyList<LocatedSection>? LocatedSections = null,

    // ── Page-scoped structure mapped 2026-09-08 (A4, A5) ────────────────────

    // Barcodes/QR codes and formulas, carried so the run report can COUNT them - see
    // BarcodeInfo and FormulaInfo for what each measurement is for and why neither is a
    // retrieval feature. Trailing and nullable, like everything else added after the fact
    // here: null on an extraction blob written before they were mapped, which is not the same
    // as "this document has none".
    IReadOnlyList<BarcodeInfo>? Barcodes = null,
    IReadOnlyList<FormulaInfo>? Formulas = null,

    // Text lines the service reported, counted for the file-facts report (2026-09-09; the
    // LineInfo list it replaces is explained on PdfDocumentStructure). Zero on a blob written
    // before the count existed.
    int LineCount = 0);
