using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Indexing.Pdf.Utils;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Computes DocumentRouting (docs/2608/260811/chunkRoutes.md step 1 for Picture,
// chunking-signals-map.md §4 for Large/Medium/Small - see ChunkRoute.cs) from the raw
// counts that decide it. Both ChunkRoute.cs and DocumentRouting.cs already referenced a
// class named exactly this in their own comments before it existed; nothing populated
// either type until pre-chunking-action-items.md item B1, page-based at first.
//
// B6 replaced the page-based Big/Normal split with the token-based Large/Medium/Small
// one chunking-signals-map.md §4 proposed - EstimatedTokens now decides everything Route
// used to get from ExtractedPageCount. Picture/sparse thresholds are untouched: they
// still match exclusion-list.md/chunkRoutes.md exactly, and must stay identical to the
// frozen exclusion list used for the strategy comparison, so they're constants, not
// per-caller configuration - same as before B6, no change here.
internal static class ChunkRoutingHelper
{
    private const double SparseCharsPerPageThreshold   = 1_000;
    private const double HighLossBytesPerCharThreshold = 100;

    // Decision 3: a document needs a navigation summary when its sections compete against
    // each other in a flat ranking, which is a section-count question, not a size one. Set
    // where the corpus itself separates: two-thirds of its 2,105 headings sit in four
    // documents, and this picks out roughly those.
    //
    // The old Large/Medium/Small token thresholds (50,000 / 4,000) are gone with the
    // ChunkRoute enum. The 50,000 line did rest on a real observation - it sat in the
    // largest gap in the corpus's own token distribution, ~25,100 to ~90,900 with nothing
    // between - and that observation is recorded in docs/2608/260812/action-plan.md (Q5) so
    // it survives the constant. The 4,000 line had no such backing and is replaced by
    // Decision 2's measured return bound.
    private const int NavigationSummaryHeadingThreshold = 100;

    public static DocumentRouting Compute(
        IReadOnlyList<PdfPageRecord> pages,
        IReadOnlyList<FigureInfo> figures,
        long fileSizeBytes,
        IReadOnlyList<Heading>? headings = null,
        IReadOnlyList<Heading>? boilerplate = null,
        IReadOnlyList<SelectionMarkInfo>? selectionMarks = null)
    {
        headings       ??= [];
        boilerplate    ??= [];
        selectionMarks ??= [];
        var pageCount  = pages.Count;
        var totalChars = pages.Sum(p => p.PageContent.Length);

        // A routing computation must never throw over a shape another gate is supposed to
        // have already caught (DI returning zero pages fails validation upstream - see
        // PdfDocumentIntelligenceAnalyzer.ValidateAnalyzeResult) - guarded here anyway rather
        // than trusting that invariant holds by the time this runs.
        var charsPerPage = pageCount == 0 ? 0 : (double)totalChars / pageCount;

        // Zero extractable characters from a non-empty file is, by definition, total
        // extraction loss - PositiveInfinity reliably routes to Picture below without a
        // divide-by-zero, rather than needing its own branch.
        var bytesPerChar = totalChars == 0 ? double.PositiveInfinity : (double)fileSizeBytes / totalChars;

        var figuresPerPage = pageCount == 0 ? 0 : (double)figures.Count / pageCount;

        // Same per-block prose/table split ChunkingHelper.SplitIntoBlocks/EstimateTokens
        // use to compute B2's real per-chunk TokenCount, applied here at document scope
        // instead of per-chunk - the routing decision and the actual token counts chunking
        // later produces are never two different numbers for the same content.
        var estimatedTokens = pages.Sum(p => ChunkingHelper.SplitIntoBlocks(p.PageContent)
            .Sum(block => ChunkingHelper.EstimateTokens(block.Text, block.IsTable)));

        // Decision 1 - the extraction gate. Same two frozen thresholds as before; the only
        // change is that this is now its own answer rather than one branch of a four-way
        // ternary that also decided size.
        var hasExtractableContent =
            charsPerPage >= SparseCharsPerPageThreshold && bytesPerChar < HighLossBytesPerCharThreshold;

        // Decision 3 - navigation grain, on section count.
        var needsNavigationSummary = headings.Count >= NavigationSummaryHeadingThreshold;

        var headingsPerThousandChars = totalChars == 0 ? 0 : headings.Count / (totalChars / 1_000.0);

        var numberedHeadingShare = headings.Count == 0
            ? 0
            : headings.Count(h => GetHeadingsHelper.NumberedHeadingPrefix().IsMatch(h.Content.Trim())) / (double)headings.Count;

        var maxSectionSizeChars = MaxGap(headings, totalChars);

        // A2 - boilerplate paragraphs carry their own text (pageHeader/footer content
        // etc.), so their share of TotalChars is a direct furniture-vs-content ratio, not
        // a proxy needing its own scale.
        var boilerplateShare = totalChars == 0
            ? 0
            : boilerplate.Sum(b => b.Content.Length) / (double)totalChars;

        var selectionMarksPerPage = pageCount == 0 ? 0 : (double)selectionMarks.Count / pageCount;

        return new DocumentRouting(
            pageCount, totalChars, fileSizeBytes, charsPerPage, bytesPerChar, figuresPerPage,
            estimatedTokens,
            hasExtractableContent,
            // Decision 2 stays unanswered until the return bound is measured (Phase D).
            // Null rather than a default: the value it replaces was reasoned and never
            // verified, and it decides what gets stored.
            DocumentIsSafeReturnUnit: null,
            needsNavigationSummary,
            headingsPerThousandChars, numberedHeadingShare, maxSectionSizeChars,
            boilerplateShare, selectionMarksPerPage);
    }

    // Widest span any single section boundary would have to cover: document start (0) and
    // end (totalChars) act as implicit heading positions bracketing the real ones, so a
    // document with zero headings correctly reports totalChars (one section, the whole
    // document), not 0 - and the tail after the last real heading is covered the same way
    // as every gap between two real headings, not treated as a special case.
    private static int MaxGap(IReadOnlyList<Heading> headings, int totalChars)
    {
        var offsets = headings
            .Where(h => h.Offset.HasValue)
            .Select(h => h.Offset!.Value)
            .Append(0)
            .Append(totalChars)
            .Distinct()
            .OrderBy(o => o)
            .ToList();

        var maxGap = 0;
        for (var i = 1; i < offsets.Count; i++)
            maxGap = Math.Max(maxGap, offsets[i] - offsets[i - 1]);

        return maxGap;
    }
}
