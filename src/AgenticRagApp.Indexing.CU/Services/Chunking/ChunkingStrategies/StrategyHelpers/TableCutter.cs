using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

// Cuts a table on ROW boundaries, repeating the header on every fragment.
//
// A header-less run of numbers means nothing to the embedder or to the model reading the
// retrieved chunk - "1.847" is not an answer to anything. So every continuation fragment carries
// the opening markup, the caption and the header rows, which is what keeps the fragment valid
// HTML rather than a headerless remainder.
//
// HTML ONLY (2026-09-09). Content Understanding writes tables as HTML (tableFormat is fixed at
// html on the prebuilt) and without regard for newlines - the whole table can arrive on one
// line - so rows are `</tr>` boundaries wherever they fall. A GFM path existed here until
// 2026-09-09 for the Document Intelligence output that preceded CU; it was kept "in case any
// source emits it", no source does, and it went with the regex detection it belonged to.
//
// The block handed in IS a table: BlockParser took it off the typed span. This class reads the
// markup only for the row geometry (TableMarkup), never to decide whether it is looking at one.
//
// Never overlapped: repeating DATA rows across two fragments duplicates records, which is not
// the same thing as restoring context.
//
// EVERY fragment is composed, including the first: even a fragment that starts at the table's
// opening tag has to be closed with markup that lives at the far end of the table, so none of
// them is a pure slice - see PieceFactory.Composed. Start/Length address the fragment's own
// DATA ROWS, which is what keeps page attribution honest for a table spanning a page break -
// the whole point of cutting it on rows.
public static class TableCutter
{
    public static IReadOnlyList<ContentPiece> Cut(ContentBlock block, int ceiling)
    {
        // Fits whole - the common case even for tables, and the only case that stays a pure
        // slice from the first character to the last.
        if (TokenEstimator.Estimate(block.Text) <= ceiling)
            return [PieceFactory.Whole(block, BoundaryLevel.None)];

        var text = block.Text;
        var rows = TableMarkup.Rows(text);
        if (rows.Count == 0) return [PieceFactory.Whole(block, BoundaryLevel.None, degraded: true)];

        var headerEnd = TableMarkup.HeaderEnd(text, rows);
        var tableEnd  = TableMarkup.TableEnd(text);

        // Repeated on every fragment: the opening tag, the caption (a table chunk without its
        // caption loses most of what makes it findable - see TableInfo), any `<thead>`/`<tbody>`
        // openings, and the header rows themselves. Leading whitespace is dropped because this
        // is composed text, not a slice.
        var prefix = text[..headerEnd].TrimStart();

        var dataRows = rows.Where(r => r.Start >= headerEnd).ToList();
        if (dataRows.Count == 0) return [PieceFactory.Whole(block, BoundaryLevel.None, degraded: true)];

        // The closing markup, verbatim: `</tbody></table>` and whatever else sits between the
        // last row and the end of the table.
        var suffix = text[Math.Min(dataRows[^1].End, tableEnd)..tableEnd];

        // Anything after `</table>` on the same line - CU writes table footnotes as text after
        // the table, and a single-line table puts them inside this block. Carried on the LAST
        // fragment only, so it is neither dropped nor duplicated.
        var tail = text[tableEnd..];

        var prefixTokens = TokenEstimator.Estimate(prefix + suffix);

        var fragments = new List<(int Start, int End, bool Degraded)>();
        int? start  = null;
        var  end    = 0;
        var  tokens = prefixTokens;

        foreach (var row in dataRows)
        {
            var rowTokens = TokenEstimator.Estimate(text[row.Start..row.End]);

            if (start.HasValue && tokens + rowTokens > ceiling)
            {
                fragments.Add((start.Value, end, false));
                start  = null;
                tokens = prefixTokens;
            }

            start ??= row.Start;
            end     = row.End;
            tokens += rowTokens;

            // One row over the ceiling on its own: emitted whole and flagged. Cutting inside it
            // would corrupt the column alignment, and a corrupt row is worse than an oversized
            // chunk - the reader cannot tell which column a value belongs to.
            if (start == row.Start && prefixTokens + rowTokens > ceiling)
            {
                fragments.Add((start.Value, end, true));
                start  = null;
                tokens = prefixTokens;
            }
        }

        if (start.HasValue) fragments.Add((start.Value, end, false));

        if (fragments.Count == 0)
            return [PieceFactory.Whole(block, BoundaryLevel.None, degraded: true)];

        return [.. fragments.Select((f, i) => PieceFactory.Composed(
            block,
            prefix + text[f.Start..f.End] + suffix + (i == fragments.Count - 1 ? tail : ""),
            f.Start,
            f.End,
            BoundaryLevel.TableRow,
            f.Degraded))];
    }
}
