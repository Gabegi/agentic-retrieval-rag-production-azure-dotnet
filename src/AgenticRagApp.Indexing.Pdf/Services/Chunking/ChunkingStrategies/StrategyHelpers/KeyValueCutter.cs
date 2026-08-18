using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Cuts a key-value run between WHOLE pairs, and never inside one.
//
// The draft says a key-value unit is never cut, and this honours that literally: the unit is the
// PAIR, and no cut ever falls between a label and its value. What the draft does not have to
// answer, because its blocks were single pairs, is what to do with a 60-pair form - and emitting
// that as one oversized chunk would put a whole document behind a single vector. So the run is
// packed to the ceiling and cut at pair boundaries.
//
// The adjacent-line form is why the boundaries are computed here rather than taken from
// LineSpans directly: after a bare "Label:", the following line is its value and is not a
// boundary at all.
public static class KeyValueCutter
{
    public static IReadOnlyList<ContentPiece> Cut(ContentBlock block, int ceiling)
    {
        if (TokenEstimator.Estimate(block.Text) <= ceiling)
            return [PieceFactory.Whole(block, BoundaryLevel.None)];

        var lines  = LineSpans.NonBlank(block.Text);
        var pieces = SpanCutter.Between(
            block, PairStarts(block, lines).Skip(1), BoundaryLevel.ListItem, ceiling);

        // One pair bigger than the whole ceiling - a label over a wall of text. Kept whole and
        // flagged: splitting it is the one thing this cutter exists to refuse.
        return pieces
            .Select(piece => TokenEstimator.Estimate(piece.Text) > ceiling
                ? piece with { Degraded = true }
                : piece)
            .ToList();
    }

    private static IEnumerable<int> PairStarts(ContentBlock block, List<(int Start, int End)> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            yield return lines[i].Start;

            // A bare label owns the next line, so that line cannot open a pair of its own.
            if (KeyValueDetector.IsLabel(block.Text[lines[i].Start..lines[i].End])) i++;
        }
    }
}
