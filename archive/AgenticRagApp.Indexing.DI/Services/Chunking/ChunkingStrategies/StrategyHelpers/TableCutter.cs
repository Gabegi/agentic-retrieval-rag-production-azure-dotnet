using AgenticRagApp.Indexing.DI.Models;

namespace AgenticRagApp.Indexing.DI.Services;

// Cuts a table on ROW boundaries, repeating the header on every fragment.
//
// A header-less run of numbers means nothing to the embedder or to the model reading the
// retrieved chunk - "1.847" is not an answer to anything. So every continuation fragment carries
// the header row and, when the table has one, the separator row, which is what keeps the
// fragment valid markdown rather than a headerless remainder.
//
// Never overlapped: repeating DATA rows across two fragments duplicates records, which is not
// the same thing as restoring context.
//
// The repeated header is the one place in this pipeline where a piece's Text is composed rather
// than sliced - see PieceFactory.Composed. Start and Length keep addressing the rows the
// fragment actually carries, so page attribution still lands on the right pages.
public static class TableCutter
{
    public static IReadOnlyList<ContentPiece> Cut(ContentBlock block, int ceiling)
    {
        // Fits whole - the common case even for tables, and the only case that stays a pure
        // slice from the first character to the last.
        if (TokenEstimator.Estimate(block.Text) <= ceiling)
            return [PieceFactory.Whole(block, BoundaryLevel.None)];

        var lines = LineSpans.NonBlank(block.Text);
        if (lines.Count == 0) return [];

        var headerCount  = Math.Min(TableDetector.HeaderLineCount(block), lines.Count);
        var headerStart  = lines[0].Start;
        var header       = block.Text[headerStart..lines[headerCount - 1].End];
        var headerTokens = TokenEstimator.Estimate(header);

        var pieces = new List<ContentPiece>();
        var first  = true;

        // The rows accumulated into the fragment being built, in local coordinates.
        int? start  = null;
        var  end    = 0;
        var  tokens = headerTokens;

        foreach (var row in lines.Skip(headerCount))
        {
            var rowTokens = TokenEstimator.Estimate(block.Text[row.Start..row.End]);

            if (start.HasValue && tokens + rowTokens > ceiling)
            {
                pieces.Add(Fragment(block, header, headerStart, start.Value, end, first, degraded: false));
                first  = false;
                start  = null;
                tokens = headerTokens;
            }

            start ??= row.Start;
            end     = row.End;
            tokens += rowTokens;

            // One row that alone breaches the ceiling is emitted whole and flagged. Cutting
            // inside it would corrupt the column alignment, and a corrupt row is worse than an
            // oversized chunk - the reader cannot tell which column a value belongs to.
            if (start == row.Start && headerTokens + rowTokens > ceiling)
            {
                pieces.Add(Fragment(block, header, headerStart, start.Value, end, first, degraded: true));
                first  = false;
                start  = null;
                tokens = headerTokens;
            }
        }

        if (start.HasValue)
            pieces.Add(Fragment(block, header, headerStart, start.Value, end, first, degraded: false));

        // A table with a header and no data rows at all: keep it whole rather than lose it.
        return pieces.Count > 0 ? pieces : [PieceFactory.Whole(block, BoundaryLevel.None, degraded: true)];
    }

    // The FIRST fragment already begins at the header, so it is a pure slice. Every later one
    // has the header prepended and is therefore composed.
    private static ContentPiece Fragment(
        ContentBlock block, string header, int headerStart, int start, int end, bool first, bool degraded) =>
        first
            ? PieceFactory.Piece(block, headerStart, end, BoundaryLevel.TableRow, degraded)
            : PieceFactory.Composed(
                  block,
                  $"{header}\n{block.Text[start..end]}",
                  start,
                  end,
                  BoundaryLevel.TableRow,
                  degraded);
}
