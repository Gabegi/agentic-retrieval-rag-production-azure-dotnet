using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

// Step 7c since 2026-09-23 (D224 A4): the rung below sentence ends.
//
// This was the first rung below a paragraph, on the premise that a line break inside a
// paragraph is "usually a wrapped clause, a sub-item, or a line of a stripped table". Under
// Content Understanding that premise does not hold: CU keeps the PDF's visual line wraps as
// single newlines inside a paragraph, so a line break is where the typesetter ran out of width,
// not where a clause ended - both Line cuts on run 260922/3 broke a sentence in half (D223 F4).
// The sentence rung now goes first; this rung takes what it cannot fit - address lists, label
// runs, a single sentence over the ceiling - where a line is still the best boundary left.
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
