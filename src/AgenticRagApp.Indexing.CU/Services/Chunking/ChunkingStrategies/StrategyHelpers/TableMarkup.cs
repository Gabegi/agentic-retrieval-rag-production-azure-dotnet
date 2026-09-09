using System.Text.RegularExpressions;

namespace AgenticRagApp.Indexing.CU.Services;

// The geometry of Content Understanding's HTML table markup, for TableCutter: where the rows
// are, where the repeated header ends, where the table ends. Positions rather than substrings,
// for the same reason the cutters produce index pairs - every fragment's coordinates have to
// keep addressing the characters it carries.
//
// This is NOT detection. Which ranges of the markdown are tables is typed (DocumentTable.Span,
// carried as TableInfo.Offset/Length) and BlockParser takes its table blocks from those spans
// (2026-09-09). The class this replaces, TableDetector, regex-detected `<table>` runs - and
// before 2026-09-08 GFM pipe rows, which CU never emits (tableFormat is fixed at html) - so the
// same range was being found twice, once by the service and once by us. The markup read here
// is the service's own rendering of a range the service already said is a table.
//
// Reading the row tags is the one thing left to do on text: cutting a table on row boundaries
// needs the `<tr>` positions, and CU types the cells (DocumentTableCell.Span) but not the row
// markup between them. If that ever changes this class goes too.
public static partial class TableMarkup
{
    [GeneratedRegex(@"<tr\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex RowOpen();

    [GeneratedRegex(@"</tr\s*>", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex RowClose();

    [GeneratedRegex(@"<th\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex HeaderCell();

    [GeneratedRegex(@"</thead\s*>", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex HeadClose();

    [GeneratedRegex(@"</table\s*>", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex TableClose();

    // The (start, end) of every `<tr> ... </tr>` in the text, in order. A row missing its
    // closing tag is not reported: the cutter can only cut on a boundary the markup declares.
    public static IReadOnlyList<(int Start, int End)> Rows(string text)
    {
        var opens = RowOpen().Matches(text).Select(m => m.Index).ToList();
        var ends  = RowEnds(text);
        var rows  = new List<(int Start, int End)>();

        var next = 0;
        foreach (var end in ends)
        {
            // The last unconsumed opening tag before this closing tag. Anything else would pair
            // tags across each other on malformed markup.
            var open = -1;
            while (next < opens.Count && opens[next] < end)
            {
                open = opens[next];
                next++;
            }

            if (open >= 0) rows.Add((open, end));
        }

        return rows;
    }

    // Where the repeated part of an HTML table ends: after the header rows when it has any,
    // otherwise just before the first row - which still carries `<table>`, `<caption>` and
    // `<thead>`/`<tbody>` openings, all of which a fragment needs to be well-formed and, in the
    // caption's case, findable at all (see TableInfo on captions).
    //
    // Header rows are the ones inside `<thead>` when that is present, else the leading rows that
    // contain a `<th>` cell. No guessing beyond that: a table whose first row is data gets no
    // repeated row, which is honest - repeating a data row would duplicate a record.
    public static int HeaderEnd(string text, IReadOnlyList<(int Start, int End)> rows)
    {
        if (rows.Count == 0) return 0;

        var headClose = HeadClose().Match(text);
        if (headClose.Success)
        {
            var end = headClose.Index + headClose.Length;
            // Rows after </thead> are data rows; the header ends where the element does.
            return rows.Any(r => r.End <= end) ? end : rows[0].Start;
        }

        var headerRows = 0;
        while (headerRows < rows.Count &&
               HeaderCell().IsMatch(text[rows[headerRows].Start..rows[headerRows].End]))
            headerRows++;

        return headerRows == 0 ? rows[0].Start : rows[headerRows - 1].End;
    }

    // Where the table's own markup ends - the last `</table>` in the text, or the end of the
    // text when the block was cut off without one. Everything from the last row to here is the
    // closing markup a fragment has to repeat (`</tbody></table>`, verbatim).
    public static int TableEnd(string text)
    {
        var closes = TableClose().Matches(text);

        return closes.Count > 0
            ? closes[^1].Index + closes[^1].Length
            : text.Length;
    }

    private static List<int> RowEnds(string text) =>
        [.. RowClose().Matches(text).Select(m => m.Index + m.Length)];
}
