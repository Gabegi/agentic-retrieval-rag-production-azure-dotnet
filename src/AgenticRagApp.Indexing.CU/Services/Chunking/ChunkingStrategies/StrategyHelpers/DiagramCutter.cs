using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

// Cuts a fenced diagram block between WHOLE ELEMENTS - after a JSON container's comma, or at a
// line start - and never inside one.
//
// Why this rung exists (D209, D214): CU writes a flowchart as one line of JSON, and its edges
// array - `{"from":"F","to":"Q","label":"Nee"},{…}` for 1,487 characters in the document that
// failed run 260921/1 - carries no whitespace. The prose ladder's word rung fails on a single
// gap-free segment over the ceiling, the whole unit drops to HardCutter, and the tripwire in
// ChunkingService reads the mid-word fragments as a broken extraction. The extraction was not
// broken; the block offered 84 `},` boundaries no rung knew about. This rung knows about them,
// so HardCutter is UNREACHABLE from a diagram block: an element that alone exceeds the ceiling
// is emitted whole and flagged Degraded (the ListRunCutter rule), not sent down the ladder.
//
// The block handed in IS a diagram: BlockParser took it off a complete fence (DiagramMarkup).
// This class reads the markup only for the geometry, never to decide whether it is looking at
// one - the same division of labour as TableCutter and TableMarkup.
//
// EVERY FRAGMENT IS A PURE SLICE. The opener line (` ```mermaid `) belongs to the first
// fragment and the closer to the last; nothing is repeated, nothing is composed. ContentPiece
// documents exactly two exemptions from the slice invariant and this cutter adds no third
// (D211 decision 5). A fragment is therefore not valid JSON or valid Mermaid on its own, and
// does not need to be - it is embedded and retrieved as text, and putting the diagram back
// together is split-linkage's job (D211 §5), not the cutter's. What a fragment 2..n LACKS is
// a semantic head of its own; D214 §2.6 puts the figure's caption or description into the
// embedded prefix of cut fragments for that reason, priced by the cascade before this runs.
//
// No overlap. Repeating elements across two fragments duplicates nodes rather than restoring
// context.
public static class DiagramCutter
{
    // contextTokens is what the figure context (DiagramContext) will cost in the prefix of every
    // CUT fragment, priced by the cascade. It is charged only on the cut path: the fits-whole
    // test runs against the full ceiling because a whole block gets no context. Floored at
    // MinBodyTokenBudget exactly as the strategies floor the prefix - when the floor binds, the
    // ceiling is breached by choice, which is the existing contract of that constant.
    public static IReadOnlyList<ContentPiece> Cut(ContentBlock block, int ceiling, int contextTokens = 0)
    {
        // Fits whole - measured on run 260921/1, 644 of 718 blocks (90%), and the only case
        // that stays one piece from the opener to the closer.
        if (TokenEstimator.Estimate(block.Text) <= ceiling)
            return [PieceFactory.Whole(block, BoundaryLevel.None)];

        ceiling = Math.Max(ceiling - contextTokens, ChunkingBudget.MinBodyTokenBudget);

        // The parser declared this block off a complete fence, so one is here. If it is not,
        // the block has no usable geometry and is kept whole and flagged - the same "cannot
        // cut on a boundary the markup does not declare" path TableCutter takes on a table
        // with no rows.
        var fences = DiagramMarkup.Fences(block.Text);
        if (fences.Count == 0) return [PieceFactory.Whole(block, BoundaryLevel.None, degraded: true)];

        var (bodyStart, bodyEnd) = DiagramMarkup.Body(block.Text, fences[0]);
        var boundaries = DiagramMarkup.Boundaries(block.Text, bodyStart, bodyEnd);

        var pieces = SpanCutter.Between(block, boundaries, BoundaryLevel.DiagramElement, ceiling);

        // A single element longer than the ceiling comes back as its own oversized piece.
        // Flagged rather than cut: the reader cannot tell half a node label from a whole one,
        // and Degraded is how the breach is counted instead of hidden. Measured: 8 of 718
        // blocks hold such an element, 2 of them over 1,200 characters.
        return pieces
            .Select(piece => TokenEstimator.Estimate(piece.Text) > ceiling
                ? piece with { Degraded = true }
                : piece)
            .ToList();
    }
}
