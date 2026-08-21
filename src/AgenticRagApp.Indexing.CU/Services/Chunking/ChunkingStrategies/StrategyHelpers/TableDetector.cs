using System.Text.RegularExpressions;

namespace AgenticRagApp.Indexing.CU.Services;

// Step 3 of the recursive route: is this block a table?
//
// Two levels, both here on purpose. BlockParser needs the LINE test to group a run in the first
// place; RecursiveStrategy needs the BLOCK test to dispatch that run to TableCutter. Splitting
// them across two files is how "what counts as a table" ends up meaning two different things at
// the two ends of the same pipeline.
public static partial class TableDetector
{
    // A markdown table row: pipe, anything, pipe. Document Intelligence emits tables in GFM,
    // so this is the shape the whole corpus arrives in.
    [GeneratedRegex(@"^\s*\|.*\|\s*$", RegexOptions.Compiled)]
    private static partial Regex TableRowLine();

    public static bool IsRow(string line) => TableRowLine().IsMatch(line);

    // A GFM separator row - "|---|:---:|---:|" - every cell between pipes containing only
    // dashes, colons and whitespace. Identifying it is what lets TableCutter repeat the header
    // AND the separator on every fragment, so a continuation fragment is still a valid table
    // rather than a run of numbers.
    public static bool IsSeparator(string line)
    {
        var cells = line.Split('|', StringSplitOptions.RemoveEmptyEntries);

        return cells.Length > 0 &&
               cells.All(cell => cell.Trim().Length > 0 && cell.Trim().All(c => c is '-' or ':'));
    }

    // At least TWO consecutive rows, all of them rows. One line that happens to contain pipes is
    // a sentence with pipes in it - the same rule (and the same reason) as the list run test.
    // A separator row is not required: plenty of real tables arrive as header plus data only.
    public static bool IsTable(ContentBlock block)
    {
        var lines = block.NonBlankLines();

        return lines.Count >= 2 && lines.All(IsRow);
    }

    // How many leading lines are the header: two when a separator row follows the header row,
    // one otherwise. TableCutter repeats exactly these on every continuation fragment.
    public static int HeaderLineCount(ContentBlock block)
    {
        var lines = block.NonBlankLines();

        return lines.Count > 1 && IsSeparator(lines[1]) ? 2 : 1;
    }
}
