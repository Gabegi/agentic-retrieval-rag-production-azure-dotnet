using System.Text.RegularExpressions;
using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

// How much of a cut is figure description (2026-09-15). CU writes each figure's generated
// description into the markdown as image alt text - `![description](figures/N.M "...")` - so it
// rides inside chunk Content and gets embedded like any other text. docs/2609/260909/
// cu-figures-demonstration.md measured by hand that 341 of 3,940 chunks carry it, 117 are at
// least half of it, 23 at least 90%, and 97 are little more than a page-header/footer logo's
// description. These two counts put that on every run report instead.
//
// Both are measurements, not routing: nothing here drops or rewrites anything. Routing on
// FigureInfo.Role is the open item D183 names; this is what would show whether it worked.
public static partial class FigureTextCounter
{
    // The alt text of a markdown image, up to the first ']' - CU descriptions contain none.
    [GeneratedRegex(@"!\[([^\]]*)\]\(")]
    private static partial Regex AltText();

    // Characters of Content that are image alt text.
    public static int AltTextChars(string content) =>
        AltText().Matches(content).Sum(m => m.Groups[1].Length);

    // Characters of Content that are the description of a pageHeader / pageFooter figure on the
    // cut's pages. Matched by the description TEXT, because a figure carries no span - only the
    // description CU generated for it, which is exactly what lands in the markdown. Descriptions
    // are de-duplicated first: the same logo on each of a cut's pages is one FigureInfo per page
    // with an identical description, and counting each entry's occurrences would double-count a
    // cut that spans two of them.
    public static int HeaderFooterDescriptionChars(string content, IReadOnlyList<FigureInfo> figures)
    {
        var descriptions = figures
            .Where(f => IsHeaderOrFooter(f.Role) && !string.IsNullOrEmpty(f.Description))
            .Select(f => f.Description!)
            .Distinct(StringComparer.Ordinal);

        var total = 0;
        foreach (var description in descriptions)
        {
            var at = 0;
            while ((at = content.IndexOf(description, at, StringComparison.Ordinal)) >= 0)
            {
                total += description.Length;
                at    += description.Length;
            }
        }
        return total;
    }

    // Role is CU's DocumentFigure.Role as a string (CuFigureHelper); compared case-insensitively
    // so a casing change on the wire does not silently zero the count.
    private static bool IsHeaderOrFooter(string? role) =>
        string.Equals(role, "pageHeader", StringComparison.OrdinalIgnoreCase)
        || string.Equals(role, "pageFooter", StringComparison.OrdinalIgnoreCase);
}
