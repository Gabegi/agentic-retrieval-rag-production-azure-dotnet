using AgenticRagApp.Indexing.Pdf.Utils;

namespace AgenticRagApp.Indexing.Pdf.Services;

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

    public static string Build(string? title, string? domainTag, string? headingPath)
    {
        // "Title [tag]" - shared with whatever writes the real embedded text, which is the
        // whole point of TitleLine living in ChunkingHelper rather than here.
        var titleLine = ChunkingHelper.TitleLine(title, domainTag);

        var parts = new[] { titleLine, CapPath(headingPath) }
            .Where(part => !string.IsNullOrWhiteSpace(part));

        // The last funnel before the prefix becomes embedded text, and therefore the place the
        // character repairs have to hold. Every upstream path is already repaired - titles in
        // GetTitleHelper, headings in HeadingTextNormalizer.Flatten, bodies in PdfCleaner - but
        // doc.Title does not always come from GetTitleHelper: when a document yields no usable
        // title, it falls back to the source id, and a filename never went through any of them.
        // The 260819 artifact carries "Folder Beeldzorg - informatie clie\u0308nt -zidw" - a
        // decomposed diaeresis - straight into metadata.Prefix, and Prefix is half of
        // EmbeddingText (ChunkObject.EmbeddingText), so that chunk embeds and hashes against a
        // spelling of "cliënt" no NFC query will ever match.
        //
        // Repairing here rather than at each source is the same argument HeadingLocator makes
        // for its needle: the composed string has to transform exactly like the body it is
        // joined to. Repair is idempotent (NFC of NFC is NFC), so text that arrived already
        // normalized is unchanged and its vector is unaffected.
        return ExtractedTextRepair.Repair(string.Join("\n\n", parts));
    }

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
