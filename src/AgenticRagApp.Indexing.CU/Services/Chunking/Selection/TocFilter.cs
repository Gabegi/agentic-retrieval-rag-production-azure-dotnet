using System.Text.RegularExpressions;
using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

// Is this cut a table of contents?
//
// A TOC is navigation, not content. Indexed it occupies a row, carries a heading path that
// matches half the document's vocabulary, and comes back as a match for queries about every
// section it lists while answering none of them. The 260818 review found "Inhoud Inhoud",
// "Inhoudsopgave" and "Inleiding > Definities" chunks doing exactly that.
//
// TWO signals, both required - the title AND the shape of the body.
//
// Title alone is not enough, and this is the whole reason the class exists rather than a
// one-line name check: "Inhoud van de zorgmap" is a real section about what a care folder
// holds, and "Inhoud" is a legitimate heading over a genuine list of contents in a protocol.
// Body shape alone is not enough either - a rate appendix is also mostly short lines ending
// in numbers.
//
// The direction is deliberately conservative. A TOC left in the index is a bad row; a real
// section dropped is content that cannot be retrieved at all, and nothing downstream can tell
// it was ever there. When the two signals disagree, the chunk is kept.
public static partial class TocFilter
{
    // The heading text a TOC sits under in this corpus. Whole-title match, not a substring:
    // "Inhoudsopgave" is a TOC, "Inhoud van de zorgmap" is not, and a Contains check cannot
    // tell them apart. "Inhoud Inhoud" is the observed doubled form, so a repeat of the same
    // word collapses to the same verdict.
    [GeneratedRegex(@"^\s*(inhoud|inhoudsopgave|contents|table of contents)([\s.:]+\1)*\s*$",
                    RegexOptions.IgnoreCase)]
    private static partial Regex TocTitle();

    // A TOC entry: a label, then either a run of dot leaders or a stretch of whitespace, then
    // a page number and nothing else. Both renderings appear in this corpus - DI keeps the
    // leader dots on some documents and collapses them to spaces on others.
    [GeneratedRegex(@"^\s*\S.*?[\s.]{2,}\d{1,4}\s*$")]
    private static partial Regex TocEntryLine();

    // A bare number on its own line - what a leader row degrades to when the label and its
    // page number end up on separate lines.
    [GeneratedRegex(@"^\s*\d{1,4}\s*$")]
    private static partial Regex PageNumberLine();

    // A TOC entry as Content Understanding renders it (2026-09-22): a table row whose last
    // cell is a bare page number. CU emits a table of contents as an HTML table, one cell per
    // line, so the two line shapes above never see "1.2 Begrippen ..... 4" - they see
    // <td>1.2 Begrippen</td> and <td>4</td> on separate lines and match neither. Run 260921/1
    // measured the result: TocChunksDropped 0 on 1,101 documents, 384 navigation chunks in the
    // index with both signals agreeing. Attributes are allowed on the cell because CU emits
    // colspan on 25 of those chunks.
    [GeneratedRegex(@"<tr\b[^>]*>(.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TableRow();

    [GeneratedRegex(@"<t[dh]\b[^>]*>\s*\d{1,4}\s*</t[dh]>\s*$", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex PageNumberRow();

    // How much of the body has to look like navigation. Set high because the cost of a false
    // positive is unrecoverable (see the class note) and because a real TOC is essentially
    // 100% entry lines - the margin is for a stray title or a page header caught in the cut,
    // not for genuine prose.
    private const double MinEntryShare = 0.6;

    // Below this a body is too short to have a shape at all. A two-line cut that happens to
    // end in a number is not evidence of anything.
    private const int MinLines = 3;

    public static bool IsTableOfContents(ChunkObject chunk) =>
        HasTocTitle(chunk) && LooksLikeEntries(chunk.Content);

    // The leaf heading, or the last segment of the path when the cut carries no leaf of its
    // own. Only the leaf: a real section nested UNDER a TOC entry is not a TOC, and matching
    // anywhere in the path would take the whole chapter with it.
    private static bool HasTocTitle(ChunkObject chunk)
    {
        var leaf = !string.IsNullOrWhiteSpace(chunk.HeadingText)
            ? chunk.HeadingText
            : chunk.HeadingPath?.Split(" > ", StringSplitOptions.RemoveEmptyEntries).LastOrDefault();

        return !string.IsNullOrWhiteSpace(leaf) && TocTitle().IsMatch(leaf);
    }

    // The unit is the row when the body is a table and the line otherwise - never both in one
    // denominator. Measured on 260921/1: rows alone drop 384; rows and outside lines together
    // drop 379, and the 5 they miss are real TOCs where the heading line and <br>-joined
    // double entries dilute the share. Nothing went the other way.
    private static bool LooksLikeEntries(string content)
    {
        var rows = TableRow().Matches(content);
        if (rows.Count > 0)
        {
            if (rows.Count < MinLines) return false;

            var navRows = rows.Count(row => PageNumberRow().IsMatch(row.Groups[1].Value));

            return navRows >= rows.Count * MinEntryShare;
        }

        var lines = content
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        if (lines.Count < MinLines) return false;

        // The heading line itself is not an entry and should not be counted against the share.
        var entries = lines.Count(line => TocEntryLine().IsMatch(line) || PageNumberLine().IsMatch(line));

        return entries >= lines.Count * MinEntryShare;
    }
}
