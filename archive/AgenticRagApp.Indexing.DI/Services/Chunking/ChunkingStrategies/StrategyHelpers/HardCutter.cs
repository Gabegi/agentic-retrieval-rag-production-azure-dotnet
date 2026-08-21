using AgenticRagApp.Indexing.DI.Models;
using AgenticRagApp.Indexing.DI.Utils;

namespace AgenticRagApp.Indexing.DI.Services;

// Step 7e: the terminator. Fixed-width windows, mid-word by construction.
//
// Only text that offers no boundary of any kind gets here - an unbroken token run, a base64
// blob, a table whose pipes were stripped by cleaning. The pieces it produces are bad chunks,
// and that is the point: HardCut arrivals are the fall-through metric, and a corpus producing
// them is telling you its extraction lost its separators, not that its prose is unusual.
//
// The window is sized in CHARACTERS, through ChunkingHelper.CharBudgetForTokens, which is the
// one job the chars-per-token ratio is still right for: it is set at or below the worst-case
// measured ratio, so a window is if anything smaller than the ceiling allows. That is what makes
// this rung guaranteed to terminate the ladder - it cannot hand back a piece it failed to cut.
public static class HardCutter
{
    public static IReadOnlyList<ContentPiece> Cut(ContentBlock block, int ceiling) =>
        SpanCutter.Between(block, Boundaries(block.Text, ceiling), BoundaryLevel.HardCut, ceiling);

    private static IEnumerable<int> Boundaries(string text, int ceiling)
    {
        var window = Math.Max(ChunkingHelper.CharBudgetForTokens(ceiling, isTable: false), 1);

        for (var i = window; i < text.Length; i += window)
            yield return i;
    }
}
