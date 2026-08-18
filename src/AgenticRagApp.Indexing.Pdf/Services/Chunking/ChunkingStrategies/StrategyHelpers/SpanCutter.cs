using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// The shared loop behind every ladder cutter. Each cutter's only real content is WHERE its
// boundaries are; turning boundaries into pieces is the same code four times over, and writing
// it once is what keeps the offset discipline in one place rather than in four.
//
// It CUTS AND PACKS, and the packing is not optional. Splitting a block at every boundary of a
// level - the literal reading - means one piece per line at the line level and one piece per
// WORD at the word level, which is not a chunk, it is a token. So the boundaries define where a
// cut is ALLOWED, and segments are accumulated up to the ceiling before one is taken.
//
// That also gives the cascade its stop condition for free: a segment that alone exceeds the
// ceiling ends up as its own oversized piece, CeilingCheck.AllFit reports false, and the caller
// descends to a finer level. Nothing else has to detect that case.
public static class SpanCutter
{
    // boundaries are LOCAL indices into block.Text, each one the position just PAST a break, so
    // a segment runs from the previous boundary up to this one. They must be ascending; anything
    // out of order or out of range is skipped rather than throwing, because a boundary generator
    // that emits a duplicate is a harmless nuisance, not a corrupt cut.
    //
    // Pieces that trim away to nothing (a run of blank lines) are dropped. An all-whitespace
    // block therefore yields NO pieces, which CeilingCheck.AllFit deliberately reports as "does
    // not fit" so the caller falls through instead of silently losing the text.
    public static IReadOnlyList<ContentPiece> Between(
        ContentBlock block, IEnumerable<int> boundaries, BoundaryLevel level, int ceiling)
    {
        var pieces = new List<ContentPiece>();

        // The piece being accumulated, in local coordinates.
        int? start  = null;
        var  end    = 0;
        var  tokens = 0;

        foreach (var segment in Segments(block.Text, boundaries))
        {
            var segmentTokens = TokenEstimator.Estimate(block.Text[segment.Start..segment.End]);

            // Taking this segment would breach the ceiling, so the piece closes at the last
            // boundary instead - the whole reason the boundaries exist.
            if (start.HasValue && tokens + segmentTokens > ceiling)
            {
                Add(pieces, block, start.Value, end, level);
                start  = null;
                tokens = 0;
            }

            start ??= segment.Start;
            end     = segment.End;
            tokens += segmentTokens;
        }

        if (start.HasValue) Add(pieces, block, start.Value, end, level);

        return pieces;
    }

    // Boundary indices become segment ranges. The text after the last boundary is a segment too:
    // a block that does not end on a boundary is the normal case, not an edge case.
    private static List<(int Start, int End)> Segments(string text, IEnumerable<int> boundaries)
    {
        var segments = new List<(int Start, int End)>();
        var start    = 0;

        foreach (var boundary in boundaries)
        {
            if (boundary <= start || boundary > text.Length) continue;

            segments.Add((start, boundary));
            start = boundary;
        }

        if (start < text.Length) segments.Add((start, text.Length));

        return segments;
    }

    private static void Add(
        List<ContentPiece> pieces, ContentBlock block, int start, int end, BoundaryLevel level)
    {
        var piece = PieceFactory.Piece(block, start, end, level);
        if (piece.Length > 0) pieces.Add(piece);
    }
}
