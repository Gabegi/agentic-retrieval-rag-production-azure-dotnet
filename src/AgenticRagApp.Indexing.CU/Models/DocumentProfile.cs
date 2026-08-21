namespace AgenticRagApp.Indexing.CU.Models;

// Document-level measurements and the three first-split decisions they drive
// (docs/2608/260812/action-plan.md §2.3, C7). One value per document, carried on
// PdfExtractionDocument.
//
// The ChunkRoute enum this used to carry is gone. It fused two unrelated signal families
// into one four-way value - Picture decided by density/extraction loss, Large/Medium/Small
// by an estimated token count - and so could not express "large but unstructured": such a
// document was routed Large, handed the heading rule, and failed silently. The three
// decisions below are independent and are each answered on their own signal.
//
// Medium is not represented because it never described a behaviour. It was a token band
// between two thresholds; a document either is or is not a safe return unit, and there is
// no third parent grain for a middle tier to select.
//
// The raw counts are kept alongside the derived ratios and the decisions, not just the
// decisions alone, so a threshold can be re-argued later against the same numbers without
// re-running extraction.
public sealed record DocumentProfile(
    // ExtractedPageCount/TotalChars come from the actual cleaned pages a document's chunks
    // are built from - not native PDF metadata's PageCount, which can be null and can
    // diverge from what Document Intelligence actually extracted. Page count is a
    // denominator for the per-page ratios below and an audit value; nothing routes on it
    // (chunking-signals-map.md §4 found it the weakest available size signal - IGJ
    // Toetsingskader is 5 pages and the densest document in the corpus).
    int    ExtractedPageCount,
    int    TotalChars,
    long   FileSizeBytes,
    double CharsPerPage,
    double BytesPerChar,
    double FiguresPerPage,

    // Chars-to-tokens estimate, summed from the same per-block prose/table ratio split
    // ChunkingHelper.SplitIntoBlocks/EstimateTokens use - so the routing input and the token
    // counts chunking produces are never computed two different ways.
    int EstimatedTokens,

    // ── Decision 1: extraction gate ─────────────────────────────────────────
    // Is there content at all? CharsPerPage < 1,000 OR BytesPerChar >= 100 means the text
    // is sparse or extraction lost most of it - the content likely lives in images.
    // Thresholds are frozen and must stay identical to exclusion-list.md's, because the
    // same two numbers define the frozen exclusion list the strategy comparison runs
    // against. False here is the candidate for the fallback / Content Understanding branch.
    bool HasExtractableContent,

    // ── Decision 2: parent grain ────────────────────────────────────────────
    // Is the whole document a safe unit to return? Below the bound, returning it whole
    // costs about what returning one generous chunk costs, so a parent/child hierarchy buys
    // nothing. The constraint is returned-unit size, not document size.
    //
    // Null until the return bound is measured (action-plan.md Phase D). Deliberately not
    // defaulted to a guess: the previous 4,000-token line was reasoned, never measured, and
    // it is baked into what gets stored - getting it wrong costs a reindex, so an explicit
    // "not yet known" is safer than a plausible number nothing verified.
    bool? DocumentIsSafeReturnUnit,

    // ── Decision 3: navigation grain ────────────────────────────────────────
    // Does this document need a summary above its sections? Driven by section count, not
    // token count: a document needs navigation when its sections compete against each other
    // in a flat ranking. Two-thirds of the corpus's headings sit in four documents, so this
    // is a handful of model calls in total.
    bool NeedsNavigationSummary,

    // ── Derived metrics (B3-B5) ─────────────────────────────────────────────

    // B3 - over-firing detection: a document with many heading-role paragraphs relative to
    // its size (e.g. Buddy: 10 headings on 1 page) inflates HeadingsDetected in a direction
    // that looks healthy but isn't.
    double HeadingsPerThousandChars,

    // B4 - numbered-heading share: which documents carry a numbering cross-check and which
    // don't. 0 when there are no headings at all - a document with a cross-check for none
    // of its (zero) headings is indistinguishable from one with no cross-check, so this is
    // the honest value either way, not a special case.
    double NumberedHeadingShare,

    // B5 - largest gap between headings, in characters: the widest span any single section
    // boundary would have to cover. Computed across document start (0), every heading's
    // Offset, and document end (TotalChars) as one sorted sequence, so a document with zero
    // headings correctly reports TotalChars (the whole document is one section), not 0.
    int MaxSectionSizeChars,

    // ── A2/A5: already extracted, previously never counted ──────────────────

    // A2 - share of the document's own characters that are template furniture
    // (pageHeader/pageFooter/footnote/pageNumber roles) rather than real content. Explains
    // part of the duplicate-chunk problem: a high share means much of what got extracted is
    // furniture repeated on every page.
    double BoilerplateShare,

    // A5 - selection marks (checkboxes/rating-grid cells) per page. Nonzero identifies a
    // form/checklist shape DI's own Role classification doesn't surface anywhere else, and
    // explains more of the duplicate-chunk boilerplate: a page of empty rating-grid
    // checkboxes produces near-identical low-content chunks across documents sharing a
    // template.
    double SelectionMarksPerPage,

    // Fraction of the document's characters living in table blocks, measured on the same
    // block split chunking cuts on (SplitIntoBlocks) - the "is this document table-shaped"
    // routing signal (TableChecker). Trailing with a default so snapshots written before
    // the field existed deserialize as 0, which routes as not-table-shaped - the safe
    // default - until the document is next extracted.
    double TableCharShare = 0);
