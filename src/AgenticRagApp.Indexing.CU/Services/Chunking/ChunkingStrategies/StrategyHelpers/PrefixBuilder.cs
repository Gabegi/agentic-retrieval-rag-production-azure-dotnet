using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Utils;

namespace AgenticRagApp.Indexing.CU.Services;

// The context every chunk carries into its own embedding: the document title, the sector tag,
// and (route 1 only) the heading chain.
//
// ONE rule for both the budgeted text and the embedded text. They were separate before, and a
// prefix that is priced differently from how it is written is a ceiling that does not hold -
// the strategy budgets against one string and the indexer embeds another.
//
// The composition is deliberately the same as the old ToChunk path: title line, blank line,
// heading path, blank line, body. Changing the joiner changes every vector and forces a full
// re-embed, so it is not a free choice.
public static class PrefixBuilder
{
    // The chain is capped on the PREFIX, not on the boundary: every heading still opens its own
    // section, only the embedded chain is truncated. A ten-level chain on a deep document would
    // otherwise price the body down to its floor and spend the whole ceiling on ancestry the
    // leaf levels already imply.
    private const int MaxPathLevels = 3;

    private const string PathSeparator = " > ";

    // figureContext (2026-09-22, D214 §2.6): the caption or capped description of the diagram a
    // CUT fragment came from, after the heading path. Null - every chunk that is not a diagram
    // fragment - reproduces the pre-2026-09-22 string byte for byte, so no existing vector
    // moves. Resolved by DiagramContext for both callers of this method.
    public static string Build(string? title, string? domainTag, string? headingPath, string? figureContext = null)
    {
        // "Title [tag]" - shared with whatever writes the real embedded text, which is the
        // whole point of TitleLine living in ChunkingHelper rather than here.
        var titleLine = ChunkingHelper.TitleLine(title, domainTag);

        var parts = new[] { titleLine, CapPath(headingPath), figureContext }
            .Where(part => !string.IsNullOrWhiteSpace(part));

        // This was the last funnel before the prefix becomes embedded text, and so the place the
        // character repairs were applied. Nothing repairs now - not here, and not at any upstream
        // path - so whatever form the title and heading path arrived in is what the prefix
        // carries. The known case:
        // The 260819 artifact carries "Folder Beeldzorg - informatie clie\u0308nt -zidw" - a
        // decomposed diaeresis - straight into metadata.Prefix, and Prefix is half of
        // EmbeddingText (ChunkObject.EmbeddingText), so that chunk embeds and hashes against a
        // spelling of "cliënt" no NFC query will ever match.
        return string.Join("\n\n", parts);
    }

    // What the prefix costs against the ceiling: the prefix AS EMBEDDED, joiner included
    // (2026-09-22, D211 §3.3 item 2). ChunkObject.EmbeddingText is prefix + "\n\n" + body, and
    // the strategies budgeted Estimate(prefix) alone, so the one-token joiner rode free: on run
    // 260921/1 the sum of the two estimates equalled the stamped token_count on 15.2% of chunks
    // and under-priced 84.8%; with the joiner it equals it on 31,135 of 31,135. An empty prefix
    // has no joiner and costs nothing.
    public static int Cost(string prefix) =>
        prefix.Length > 0 ? TokenEstimator.Estimate(prefix + ChunkObject.PrefixJoiner) : 0;

    // Keeps the LAST levels, not the first: the leaf and its immediate parents are what
    // disambiguate a chunk, while the root is usually the document title again.
    private static string? CapPath(string? headingPath)
    {
        if (string.IsNullOrWhiteSpace(headingPath)) return headingPath;

        var levels = headingPath.Split(PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        return levels.Length <= MaxPathLevels
            ? headingPath
            : string.Join(PathSeparator, levels[^MaxPathLevels..]);
    }
}
