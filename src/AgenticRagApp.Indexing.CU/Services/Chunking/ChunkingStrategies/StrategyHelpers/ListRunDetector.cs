using System.Text.RegularExpressions;

namespace AgenticRagApp.Indexing.CU.Services;

// Step 5 of the recursive route: is this block a list run?
//
// Bullet and numbered items are deliberately NOT distinguished (parked gap E5). What matters
// for cutting is that the run is a sequence of short peer items rather than continuous prose,
// because that is what makes a mid-item cut so much worse than a mid-paragraph one - a reader
// cannot tell a truncated instruction from a complete one.
public static partial class ListRunDetector
{
    // A bullet or a number, then whitespace, then something - with ONE deliberate exception:
    // a numbered marker alone on its line still counts as an item.
    //
    // Ported unchanged from the splitter this replaces, so a corpus that chunked one way before
    // does not silently change shape - and that is why the exception is stated rather than
    // quietly widened. PdfCleaner.OrphanedListMarker now rejoins a stranded "N." to its clause
    // upstream, so this case should not survive extraction at all; it stays here because the
    // failure it caused was disproportionate. The old pattern required content on the SAME line,
    // so one stray marker made IsListRun's lines.All(IsItem) false and dropped the entire block
    // to the prose ladder - losing whole-item cutting for every other item in the run.
    //
    // Bullets keep the strict form. A bare "-" is a stray dash far more often than it is an
    // empty list item, and it was never the measured defect (111 bare "N." lines, 260818 run).
    [GeneratedRegex(@"^\s*(?:[-*•·]\s+\S|\(?\d{1,3}[.)](?:\s+\S|\s*$))", RegexOptions.Compiled)]
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
