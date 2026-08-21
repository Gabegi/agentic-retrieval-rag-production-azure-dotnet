using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

// Step 7b: the first rung below a whole paragraph.
//
// A line break inside a paragraph is a weaker signal than a blank line but a much stronger one
// than a full stop - in this corpus it is usually a wrapped clause, a sub-item, or a line of a
// stripped table that lost its pipes. Cutting there keeps clauses intact.
public static class LineBreakCutter
{
    public static IReadOnlyList<ContentPiece> Cut(ContentBlock block, int ceiling) =>
        SpanCutter.Between(block, Boundaries(block.Text), BoundaryLevel.Line, ceiling);

    // Just past each newline, so the newline stays with the line it ends.
    private static IEnumerable<int> Boundaries(string text) =>
        LineSpans.Read(text)
            .Skip(1)
            .Select(span => span.Start);
}
