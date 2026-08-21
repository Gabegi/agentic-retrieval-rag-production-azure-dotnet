using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

// Step 7d: cut at word gaps - the last boundary the text itself offers.
//
// Reaching this rung means the block has no sentence end in it at all: an address block, a
// column of headings run together, a legal reference chain. The pieces are still packed to the
// ceiling by SpanCutter, so this produces near-ceiling chunks cut between whole words, not one
// piece per word.
public static class WordGapCutter
{
    public static IReadOnlyList<ContentPiece> Cut(ContentBlock block, int ceiling) =>
        SpanCutter.Between(block, Boundaries(block.Text), BoundaryLevel.Word, ceiling);

    // Just past each run of whitespace, so a gap never opens a piece.
    private static IEnumerable<int> Boundaries(string text)
    {
        for (var i = 1; i < text.Length; i++)
        {
            if (!char.IsWhiteSpace(text[i]) && char.IsWhiteSpace(text[i - 1]))
                yield return i;
        }
    }
}
