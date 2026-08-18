using System.Text.RegularExpressions;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Step 5 of the recursive route: is this block a list run?
//
// Bullet and numbered items are deliberately NOT distinguished (parked gap E5). What matters
// for cutting is that the run is a sequence of short peer items rather than continuous prose,
// because that is what makes a mid-item cut so much worse than a mid-paragraph one - a reader
// cannot tell a truncated instruction from a complete one.
public static partial class ListRunDetector
{
    // A bullet or a number, then whitespace, then something. Ported unchanged from the splitter
    // this replaces, so a corpus that chunked one way before does not silently change shape.
    [GeneratedRegex(@"^\s*([-*•·]|\(?\d{1,3}[.)])\s+\S", RegexOptions.Compiled)]
    private static partial Regex ListItemLine();

    public static bool IsItem(string line) => ListItemLine().IsMatch(line);

    // Two or more item lines, and nothing else. A single line starting with a dash is a
    // sentence with a dash in it.
    public static bool IsListRun(ContentBlock block)
    {
        var lines = block.NonBlankLines();

        return lines.Count >= 2 && lines.All(IsItem);
    }
}
