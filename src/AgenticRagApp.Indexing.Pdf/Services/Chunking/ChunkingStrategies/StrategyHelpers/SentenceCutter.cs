using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Step 7c: cut at sentence ends.
//
// The index-yielding twin of ChunkingHelper.SplitSentences, which returns trimmed strings and
// is therefore unusable here - a piece has to know where it came from. Same rule as the string
// version, deliberately: a sentence ends at . ! or ? FOLLOWED BY whitespace or end of text, so
// "4.2.1" and "art. 7" do not each become three sentences.
public static class SentenceCutter
{
    private static readonly char[] SentenceEnders = ['.', '!', '?'];

    public static IReadOnlyList<ContentPiece> Cut(ContentBlock block, int ceiling) =>
        SpanCutter.Between(block, Boundaries(block.Text), BoundaryLevel.Sentence, ceiling);

    // Just past the punctuation, so the full stop stays with the sentence it ends.
    private static IEnumerable<int> Boundaries(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (Array.IndexOf(SentenceEnders, text[i]) < 0) continue;

            if (i + 1 == text.Length || char.IsWhiteSpace(text[i + 1]))
                yield return i + 1;
        }
    }
}
