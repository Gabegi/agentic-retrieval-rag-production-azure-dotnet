namespace AgenticRagApp.Indexing.Pdf.Models;

// Document-level routing facts from docs/2608/260811/chunkRoutes.md - same value on every
// page of one file, same carry-along pattern PdfExtractionDocument already uses for
// Bookmarks/Sections. The raw counts (ExtractedPageCount/TotalChars/FileSizeBytes) are
// kept alongside the derived ratios and the resolved Route, not just Route alone, so the
// routing decision can be audited or re-thresholded later without recomputing it from
// data scattered across other fields.
//
// ExtractedPageCount/TotalChars come from the actual cleaned pages a document's chunks
// are built from (see ChunkRoutingHelper.Compute's caller) - not native PDF metadata's
// PageCount, which can be null and can diverge from what Document Intelligence actually
// extracted.
public sealed record DocumentRouting(
    int    ExtractedPageCount,
    int    TotalChars,
    long   FileSizeBytes,
    double CharsPerPage,
    double BytesPerChar,
    double FiguresPerPage,

    // B6 (pre-chunking-action-items.md) - chars-to-tokens estimate driving Route's
    // Large/Medium/Small split (chunking-signals-map.md §4), summed from the same
    // per-block prose/table ratio split ChunkingHelper.SplitIntoBlocks/EstimateTokens
    // already use for real chunk token counts (B2) - so the routing decision and the
    // token counts chunking actually produces are never computed two different ways.
    int EstimatedTokens,

    ChunkRoute Route,

    // ── B3-B5 (docs/2608/260811/pre-chunking-action-items.md) ───────────────
    // Derived metrics that were previously only ever computed by hand against report
    // artifacts (exclusion-list.md, hygienecode-numbering-findings.md) - now pipeline
    // fields, same reasoning as the six above them.

    // B3 - over-firing detection: a document with many heading-role paragraphs relative
    // to its size (e.g. Buddy: 10 headings on 1 page) inflates HeadingsDetected in a
    // direction that looks healthy but isn't.
    double HeadingsPerThousandChars,

    // B4 - numbered-heading share: which documents carry a numbering cross-check
    // (GetHeadingsHelper.NumberedHeadingPrefix) and which don't. 0 when there are no
    // headings at all - a document with a numbering cross-check for none of its
    // (zero) headings is indistinguishable from one with no cross-check, so this is
    // the honest value either way, not a special case.
    double NumberedHeadingShare,

    // B5 - largest gap between headings, in characters: the widest span of content any
    // single section boundary would have to cover. Computed across document start (0),
    // every heading's Offset, and document end (TotalChars) as one sorted sequence, so a
    // document with zero headings correctly reports TotalChars (the whole document is
    // one section), not 0.
    int MaxSectionSizeChars,

    // ── A2/A5 (docs/2608/260811/pre-chunking-action-items.md) ───────────────
    // Group A signals: already extracted, never counted. Same "resolve once at document
    // level" treatment as B3-B5 above.

    // A2 - share of the document's own characters that are template furniture
    // (pageHeader/pageFooter/footnote/pageNumber roles), not real content. Feeds
    // template-family detection (C2/FamilyIdEmbedder doesn't consume this yet - a
    // future signal, not wired in) and explains part of the duplicate-chunk problem:
    // a high share means a large fraction of what got extracted is furniture repeated
    // on every page, not distinct content. 0 when TotalChars is 0, same reasoning as
    // every other /TotalChars ratio here.
    double BoilerplateShare,

    // A5 - selection marks (checkboxes/rating-grid cells) per page. A nonzero value
    // identifies a form/checklist document shape DI's own Role classification doesn't
    // surface anywhere else, and also explains part of the duplicate-chunk boilerplate:
    // a page of empty rating-grid checkboxes produces near-identical low-content chunks
    // across documents that share the same form template.
    double SelectionMarksPerPage);
