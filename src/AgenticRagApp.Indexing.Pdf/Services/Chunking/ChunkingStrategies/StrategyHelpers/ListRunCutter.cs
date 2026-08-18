using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Cuts a list run between WHOLE items.
//
// The narrow case where cutting on a line boundary is exactly right: a list is a sequence of
// peer items, so the only sensible cut is between two of them. A balanced character split would
// land mid-item, and half an item is worse than an uneven chunk - the reader cannot tell a
// truncated instruction from a complete one.
//
// No overlap. Repeating whole items across two chunks duplicates instructions rather than
// restoring context, and a list item is self-contained by construction.
public static class ListRunCutter
{
    public static IReadOnlyList<ContentPiece> Cut(ContentBlock block, int ceiling)
    {
        if (TokenEstimator.Estimate(block.Text) <= ceiling)
            return [PieceFactory.Whole(block, BoundaryLevel.None)];

        var items  = LineSpans.NonBlank(block.Text);
        var pieces = SpanCutter.Between(
            block, items.Skip(1).Select(item => item.Start), BoundaryLevel.ListItem, ceiling);

        // A single item longer than the ceiling comes back as its own oversized piece. It is
        // flagged rather than cut: the draft would send it down the prose ladder, but that
        // ladder lives in the strategy, and an item split mid-sentence still reads as a whole
        // instruction. Degraded is how it is counted instead of hidden.
        return pieces
            .Select(piece => TokenEstimator.Estimate(piece.Text) > ceiling
                ? piece with { Degraded = true }
                : piece)
            .ToList();
    }
}
