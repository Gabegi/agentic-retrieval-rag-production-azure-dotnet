using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// The only place a ContentPiece is constructed, so the slice invariant has exactly one place
// to be got right:
//
//     content.AsSpan(piece.Start, piece.Length).SequenceEqual(piece.Text)
//
// A cutter never builds strings. It produces INDEX PAIRS into its block, and this turns a pair
// into a piece. That is the whole discipline - the helpers this replaces returned List<string>
// and called .Trim(), which is precisely where position was lost: a trimmed substring no longer
// starts where its Start says it does, and nothing downstream can tell.
public static class PieceFactory
{
    // start and end are LOCAL to block.Text; block.Start makes them absolute.
    //
    // Whitespace is trimmed by MOVING THE BOUNDS, never by calling Trim() on the substring, so
    // the coordinates keep addressing the exact characters that ended up in Text.
    public static ContentPiece Piece(
        ContentBlock block, int start, int end, BoundaryLevel level, bool degraded = false)
    {
        var text = block.Text;

        start = Math.Clamp(start, 0, text.Length);
        end   = Math.Clamp(end,   0, text.Length);

        while (start < end && char.IsWhiteSpace(text[start]))   start++;
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;

        return new ContentPiece(
            Text:          text[start..end],
            Start:         block.Start + start,
            Length:        end - start,
            BoundaryLevel: level,
            Degraded:      degraded);
    }

    // The documented exception: a piece whose Text was COMPOSED rather than sliced - today only
    // a table continuation fragment, which repeats the header and separator rows so that a run
    // of numbers still means something to the embedder.
    //
    // Start/Length address the underlying slice (the rows this fragment actually carries), not
    // the composed string, so page attribution in step 4 still lands on the right pages. Text
    // and Length therefore disagree here BY DESIGN - the only place in the pipeline they do.
    public static ContentPiece Composed(
        ContentBlock block, string text, int start, int end, BoundaryLevel level, bool degraded = false)
    {
        start = Math.Clamp(start, 0, block.Text.Length);
        end   = Math.Clamp(end,   0, block.Text.Length);

        return new ContentPiece(
            Text:          text,
            Start:         block.Start + start,
            Length:        Math.Max(end - start, 0),
            BoundaryLevel: level,
            Degraded:      degraded);
    }

    // A block that is kept whole, as its own single piece. Used by every cutter for the "it
    // already fits" case and by KeyValueCutter for the oversized-but-uncuttable case.
    public static ContentPiece Whole(ContentBlock block, BoundaryLevel level, bool degraded = false) =>
        Piece(block, 0, block.Text.Length, level, degraded);
}
