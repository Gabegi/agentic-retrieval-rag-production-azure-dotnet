using AgenticRagApp.Indexing.DI.Models;

namespace AgenticRagApp.Indexing.DI.Services;

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

        // A reversed pair is a CUTTER BUG, never input, so it is refused rather than absorbed.
        //
        // This already threw before the guard existed - the range operator below raised
        // ArgumentOutOfRangeException on its own - so nothing about the outcome changes here
        // except the exception type and a message that names which cutter and which boundary
        // level. What the guard buys is that the refusal is now DELIBERATE: the obvious tidy-up
        // is to clamp, as Composed does, and clamping would make a cutter that walked its
        // boundaries backwards emit a silent empty piece and lose that cut with nothing recording
        // it. Every cutter here produces ascending segments by construction; the moment one does
        // not, the ladder above it is choosing boundaries it cannot justify, and that is worth
        // stopping the document for.
        //
        // Composed keeps its clamp deliberately: its Start/Length address the underlying slice
        // of a COMPOSED string, where a degenerate range means "this fragment carries no source
        // characters" rather than "the cutter is confused".
        if (start > end)
            throw new ArgumentException(
                $"Piece bounds are reversed: start {start} > end {end} at boundary level {level} " +
                $"in a block of {text.Length} chars starting at {block.Start}. A cutter emitted a " +
                "descending segment - see PieceFactory.",
                nameof(start));

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
