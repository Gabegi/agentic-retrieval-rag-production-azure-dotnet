using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

// Step 7b since 2026-09-23 (D224 A4): the FIRST rung below a whole paragraph. Cut at sentence ends.
//
// The index-yielding twin of ChunkingHelper.SplitSentences, which returns trimmed strings and
// is therefore unusable here - a piece has to know where it came from. Rule 1 below is that
// method's rule; rule 2 is this cutter's alone (SplitSentences feeds identity text, not cuts).
//
// A sentence ends at . ! or ? when TWO things hold:
//
//   1. whitespace or end-of-text follows the ender - so "4.2.1" and "art.7" do not each become
//      three sentences (the original rule, shared with the string version);
//   2. the next non-whitespace character OPENS a sentence: an upper-case letter, a digit, or an
//      opening quote, bracket, dash or bullet (D224 A4, measured on run 260922/2).
//
// Rule 2 exists because once this rung is tried before the line rung, every Dutch abbreviation
// followed by a space became a seam: "bijv. iemand", "o.a. de", "m.b.t. het", "t.b.v. de",
// "incl. aanvraag", "evt. benodigde". The replay over the run's 615 over-ceiling prose units
// measured 72 lowercase-opening pieces from sentence cuts without the rule and 8 with it
// (27 in total, 19 of them word-rung pieces no sentence rule can reach). It is the classical
// splitter test - the next token is capitalised - not a list of abbreviations, so it does not
// depend on this corpus.
//
// What rule 2 cannot see, measured and accepted (D224 A4):
//   - an abbreviation before a NAME or an acronym still splits: "dhr. De Vries", "Ir. Jakoba
//     Mulderplein", "evt. EHBO-ers" - 22 seams on the run. Fixing that needs a list.
//   - an abbreviation before a NUMBER still splits: "art. 21", "o.b.v. 1", "d.d. 21", "dec. 22"
//     - 15 seams on the run, against 40 genuine sentence ends before a number. Dropping the
//     digit opener was measured and rejected: lowercase-opening pieces 27 -> 34, seams without
//     an ender 182 -> 222, word-rung units 37 -> 46 - worse on every axis, because the units
//     that lose their sentence boundary fall to the line and word rungs.
//
// The apostrophe is in the opener set for the Dutch clitics: "'s Avonds", "'t Is". Both the
// ASCII apostrophe and U+2019 count, since CU emits either.
public static class SentenceCutter
{
    private static readonly char[] SentenceEnders = ['.', '!', '?'];

    // Quotes, brackets, dashes and bullet glyphs: structural openers, none of them corpus words.
    private const string OpeningMarks = "\"'([\u2018\u2019\u201C\u00AB\u2022\u00B7\u25AB\u2013\u2014-";

    public static IReadOnlyList<ContentPiece> Cut(ContentBlock block, int ceiling) =>
        SpanCutter.Between(block, Boundaries(block.Text), BoundaryLevel.Sentence, ceiling);

    // Just past the punctuation, so the full stop stays with the sentence it ends.
    internal static IEnumerable<int> Boundaries(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (Array.IndexOf(SentenceEnders, text[i]) < 0) continue;

            // Rule 1: whitespace or end of text follows.
            if (i + 1 == text.Length) { yield return i + 1; continue; }
            if (!char.IsWhiteSpace(text[i + 1])) continue;

            // Rule 2: what follows opens a sentence. Trailing whitespace at end of text counts
            // as an end too - there is nothing after it to have mis-split.
            var next = i + 1;
            while (next < text.Length && char.IsWhiteSpace(text[next])) next++;

            if (next == text.Length || OpensSentence(text[next]))
                yield return i + 1;
        }
    }

    private static bool OpensSentence(char c) =>
        char.IsUpper(c) || char.IsDigit(c) || OpeningMarks.Contains(c);
}
