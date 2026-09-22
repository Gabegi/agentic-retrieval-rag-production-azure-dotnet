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

        // The closing markup, verbatim: `</tbody></table>` and whatever else sits between the
        // last row and the end of the table. Off the last row of the table, so it is the same
        // whichever rows end up repeated.
        var suffix = text[Math.Min(rows[^1].End, tableEnd)..tableEnd];

        // Repeating the header stops paying for itself when it leaves the rows less than
        // MinBodyTokenBudget (2026-09-22, D211 §3.3 item 6). Without this a head close to the
        // ceiling made EVERY row its own degraded fragment, each carrying the full head - one
        // table on run 260921/1 with a 2,116-char head produced 14 degraded fragments that way.
        // The head then shrinks to the opening markup (`<table>`, caption), exactly what a table
        // with no header rows gets, and the header rows appear once as the first data rows.
        // Nothing is lost; the continuation fragments are knowingly headerless, which item 5
        // will flag.
        if (headerEnd > rows[0].Start &&
            TokenEstimator.Estimate(text[..headerEnd].TrimStart() + suffix) > ceiling - ChunkingBudget.MinBodyTokenBudget)
            headerEnd = rows[0].Start;

        // Repeated on every fragment: the opening tag, the caption (a table chunk without its
        // caption loses most of what makes it findable - see TableInfo), any `<thead>`/`<tbody>`
        // openings, and the header rows themselves. Leading whitespace is dropped because this
        // is composed text, not a slice.
        var prefix = text[..headerEnd].TrimStart();

        var dataRows = rows.Where(r => r.Start >= headerEnd).ToList();
        if (dataRows.Count == 0) return [PieceFactory.Whole(block, BoundaryLevel.None, degraded: true)];

        // Anything after `</table>` on the same line - CU writes table footnotes as text after
        // the table, and a single-line table puts them inside this block. Carried on the LAST
        // fragment when it fits there, else as a piece of its own (below) - never dropped, never
        // duplicated, and since 2026-09-22 never unpriced.
        var tail = text[tableEnd..];

        // THE STRING A FRAGMENT IS EMITTED AS IS THE STRING IT IS PRICED AS (2026-09-22, D211
        // §3.3 item 1). Until then the loop charged Estimate(row) per row and emitted the
        // contiguous slice, so text between rows rode free: 2,463 of 2,955 TableRow chunks on
        // run 260921/1 carried unpriced inter-row text and 75 crossed the ceiling with
        // degraded=false. One function composes, the same function is priced, and the invariant
        // Estimate(piece.Text) <= ceiling || piece.Degraded holds by construction - no seam
        // metric needed. This is also the seam the representation work replaces: a pipe
        // rendering changes Compose, not the packing below it.
        string Compose(int start, int end, bool withTail) =>
            prefix + text[start..end] + suffix + (withTail ? tail : "");

        bool Fits(int start, int end, bool withTail = false) =>
            TokenEstimator.Estimate(Compose(start, end, withTail)) <= ceiling;

        // Fragments TILE [headerEnd, lastRow.End): the first begins where the repeated head
        // ends and each next one begins where the previous ended (item 3). Before this the
        // fragment ran first-row-start to last-row-end, and whatever sat between one fragment's
        // last `</tr>` and the next's `<tr` landed nowhere - 1,561 non-whitespace chars across
        // 35 tables on 260921/1.
        var fragments = new List<Fragment>();
        var  start    = headerEnd;
        int? accepted = null;   // End of the last row packed into the open fragment

        foreach (var row in dataRows)
        {
            var fits = Fits(start, row.End);

            if (!fits && accepted is int end)
            {
                fragments.Add(new Fragment(start, end, Degraded: false));
                start    = end;
                accepted = null;
                fits     = Fits(start, row.End);
            }

            accepted = row.End;

            // One row over the ceiling on its own: emitted whole and flagged. Cutting inside it
            // would corrupt the column alignment, and a corrupt row is worse than an oversized
            // chunk - the reader cannot tell which column a value belongs to.
            if (!fits)
            {
                fragments.Add(new Fragment(start, row.End, Degraded: true));
                start    = row.End;
                accepted = null;
            }
        }

        if (accepted is int last) fragments.Add(new Fragment(start, last, Degraded: false));

        if (fragments.Count == 0)
            return [PieceFactory.Whole(block, BoundaryLevel.None, degraded: true)];

        // The tail rides the last fragment when it fits there (or when that fragment is already
        // degraded - it is over regardless). Otherwise it is a piece of its own, head and closing
        // markup repeated around it so the footnote keeps its table, degraded only when it does
        // not fit even alone. Run 260921/1: 84 tables carried a real tail, max 916 chars; the
        // worst fragment it was appended to reached 720 tokens.
        if (tail.Length > 0)
        {
            var lastFragment = fragments[^1];
            if (lastFragment.Degraded || string.IsNullOrWhiteSpace(tail) ||
                Fits(lastFragment.Start, lastFragment.End, withTail: true))
                fragments[^1] = lastFragment with { CarriesTail = true };
            else
                fragments.Add(new Fragment(
                    tableEnd, text.Length,
                    Degraded: TokenEstimator.Estimate(prefix + suffix + tail) > ceiling,
                    TailOnly: true));
        }

        return [.. fragments.Select(f => PieceFactory.Composed(
            block,
            f.TailOnly ? prefix + suffix + tail : Compose(f.Start, f.End, f.CarriesTail),
            f.Start,
            f.End,
            BoundaryLevel.TableRow,
            f.Degraded))];
    }

    // Start/End are local to the block's text and address the rows (or the tail) this fragment
    // carries - never the repeated head, see PieceFactory.Composed.
    private readonly record struct Fragment(
        int Start, int End, bool Degraded, bool CarriesTail = false, bool TailOnly = false);
}
