using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Services;

using static RagApp.UnitTests.Indexing.ChunkingTestFixtures;

namespace RagApp.UnitTests.Indexing;

// D224 A4 (2026-09-23): the sentence rung goes before the line rung, and a sentence end needs
// the next token to open a sentence. Each test names the measured case it pins.
[TestClass]
public class LadderOrderTests
{
    private static List<int> Boundaries(string text) => SentenceCutter.Boundaries(text).ToList();

    private static int After(string text, string marker) => text.IndexOf(marker, StringComparison.Ordinal) + marker.Length;

    // ── the capital rule ─────────────────────────────────────────────────────

    [TestMethod]
    public void AnAbbreviationBeforeALowercaseWord_IsNotASentenceEnd()
    {
        // "bijv. iemand", "o.a. de", "m.b.t. het": the seams the plain swap produced (72 on run
        // 260922/2) and the reason rule 2 exists.
        const string text = "Neem bijv. iemand mee die o.a. de route kent. Dat helpt m.b.t. het vertrek.";

        CollectionAssert.AreEqual(new[] { After(text, "kent."), text.Length }, Boundaries(text));
    }

    [TestMethod]
    public void TheDutchClitics_OpenASentence()
    {
        // "'s Avonds" and "'t Is" start with an apostrophe and a lowercase letter; both ASCII
        // and U+2019 apostrophes occur in CU output.
        const string text = "Het is laat. 's Avonds is het stil. Toen kwam hij. ’t Is klaar.";

        CollectionAssert.AreEqual(
            new[] { After(text, "laat."), After(text, "stil."), After(text, "hij."), text.Length },
            Boundaries(text));
    }

    [TestMethod]
    public void QuotesBracketsDashesAndBullets_OpenASentence()
    {
        // Note ".)" - a full stop inside a closing bracket - is not an ender under rule 1 (no
        // whitespace follows the stop), which is unchanged; the bracket case tested here is the
        // OPENING bracket after "zij.".
        const string text = "Hij zei het. \"Nee\", zei zij. (Later bleek meer). - Punt drie. • Punt vier.";

        CollectionAssert.AreEqual(
            new[] { After(text, "het."), After(text, "zij."), After(text, "meer)."), After(text, "drie."), text.Length },
            Boundaries(text));
    }

    [TestMethod]
    public void AnAbbreviationBeforeANameOrANumber_StillSplits_KnownLimitation()
    {
        // Measured on run 260922/2 and accepted: 22 seams after a title abbreviation before a
        // capitalised name ("dhr. De Vries", "Ir. Jakoba", "evt. EHBO-ers") and 15 before a
        // number ("art. 21", "d.d. 21"). Telling these apart needs an abbreviation list, which
        // the rule deliberately does not carry. This test pins the limitation so a change here
        // is a decision, not an accident.
        const string text = "Vraag dhr. De Vries om art. 21 toe te passen.";

        CollectionAssert.AreEqual(
            new[] { After(text, "dhr."), After(text, "art."), text.Length },
            Boundaries(text));
    }

    [TestMethod]
    public void AnEnderNotFollowedByWhitespace_IsStillNotASentenceEnd()
    {
        // Rule 1 is unchanged: "4.2.1" and "art.7" stay whole.
        const string text = "Zie artikel 4.2.1 en art.7 hier.";

        CollectionAssert.AreEqual(new[] { text.Length }, Boundaries(text));
    }

    // ── the rung order ───────────────────────────────────────────────────────

    // A paragraph as CU emits it: the PDF's visual wraps are single newlines that fall
    // mid-sentence. Six sentences of eight words, each wrapped after its fourth word.
    private static string WrappedParagraph() =>
        string.Join(" ", Enumerable.Range(0, 6).Select(i =>
            $"Zin{i} woord woord woord\nwoord woord woord einde."));

    [TestMethod]
    public void AWrappedParagraphOverTheCeiling_IsCutAtSentenceEnds_NotAtLineWraps()
    {
        var text    = WrappedParagraph();
        var ceiling = Tokens(text) / 2 + 2;

        var pieces = BlockCascade.Cut(text, 0, text.Length, ceiling, []);

        Assert.IsTrue(pieces.Count > 1);
        Assert.IsTrue(pieces.All(p => p.BoundaryLevel == BoundaryLevel.Sentence),
            "the sentence rung is tried first: " + string.Join(",", pieces.Select(p => p.BoundaryLevel)));
        Assert.IsTrue(pieces.All(p => p.Text.TrimEnd().EndsWith("einde.")), "no piece ends at a wrap");
        AssertSliceInvariant(text, pieces);
    }

    [TestMethod]
    public void LinesWithoutASentenceEnd_StillCutAtLines()
    {
        // An address list or label run: no . ! ? anywhere, so the sentence rung produces one
        // oversize piece and the ladder descends to lines - the line rung is still there.
        var text    = string.Join("\n", Enumerable.Range(0, 12).Select(i => $"Regel {i} zonder einde adres {i}"));
        var ceiling = Tokens(text) / 3 + 2;

        var pieces = BlockCascade.Cut(text, 0, text.Length, ceiling, []);

        Assert.IsTrue(pieces.Count > 1);
        Assert.IsTrue(pieces.All(p => p.BoundaryLevel == BoundaryLevel.Line),
            string.Join(",", pieces.Select(p => p.BoundaryLevel)));
        AssertSliceInvariant(text, pieces);
    }
}
