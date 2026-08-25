using AgenticRagApp.Indexing.CU.Services;

using static RagApp.UnitTests.Indexing.ChunkingTestFixtures;

namespace RagApp.UnitTests.Indexing;

// The context every chunk carries into its own embedding. ONE rule for both the budgeted text
// and the embedded text: the strategy prices this string against the ceiling before cutting and
// ChunkMetadataBuilder stamps the same call's output onto the chunk, so a prefix that is priced
// differently from how it is written is a ceiling that does not hold.
//
// The composition is not a free choice either - changing the joiner changes every vector and
// forces a full re-embed.
[TestClass]
public class PrefixBuilderTests
{
    [TestMethod]
    public void TitleAndTagBecomeTheTitleLine()
    {
        Assert.AreEqual("CAO GGZ [ggz]", PrefixBuilder.Build("CAO GGZ", "ggz", headingPath: null));
    }

    [TestMethod]
    public void NoTag_LeavesTheTitleAlone_WithoutAnEmptyBracket()
    {
        Assert.AreEqual("CAO GGZ", PrefixBuilder.Build("CAO GGZ", domainTag: null, headingPath: null));
        Assert.AreEqual("CAO GGZ", PrefixBuilder.Build("CAO GGZ", domainTag: "", headingPath: null));
    }

    [TestMethod]
    public void TitleLineAndPathAreJoinedByABlankLine()
    {
        // The exact joiner the old ToChunk path produced. Every stored vector was computed
        // against it.
        Assert.AreEqual(
            "CAO GGZ [ggz]\n\nHoofdstuk 3 > 3.2 Dosering",
            PrefixBuilder.Build("CAO GGZ", "ggz", "Hoofdstuk 3 > 3.2 Dosering"));
    }

    [TestMethod]
    public void APathDeeperThanThreeLevels_KeepsTheLastThree()
    {
        // The leaf and its immediate parents are what disambiguate a chunk; the root is usually
        // the document title again. Capped on the PREFIX, not on the boundary - every heading
        // still opens its own section.
        var path = "Deel A > Hoofdstuk 3 > Paragraaf 3.2 > 3.2.1 Dosering > Uitzonderingen";

        var prefix = PrefixBuilder.Build("CAO GGZ", null, path);

        Assert.AreEqual("CAO GGZ\n\nParagraaf 3.2 > 3.2.1 Dosering > Uitzonderingen", prefix);
    }

    [TestMethod]
    public void APathOfExactlyThreeLevels_IsUntouched()
    {
        // On the boundary, so an off-by-one in the cap cannot pass.
        var path = "Hoofdstuk 3 > 3.2 Dosering > Uitzonderingen";

        StringAssert.EndsWith(PrefixBuilder.Build("CAO GGZ", null, path), path);
    }

    [TestMethod]
    public void ABlankOrNullPart_DropsOutWithoutLeavingAStrayJoiner()
    {
        // A stray "\n\n" would be priced against the ceiling and embedded, so it is not
        // cosmetic.
        Assert.AreEqual("Hoofdstuk 3", PrefixBuilder.Build(title: "", domainTag: null, headingPath: "Hoofdstuk 3"));
        Assert.AreEqual("Hoofdstuk 3", PrefixBuilder.Build(title: null, domainTag: null, headingPath: "Hoofdstuk 3"));
        Assert.AreEqual("CAO GGZ",     PrefixBuilder.Build("CAO GGZ", null, headingPath: "   "));
    }

    [TestMethod]
    public void NothingAtAll_IsAnEmptyPrefix_NotAJoiner()
    {
        Assert.AreEqual("", PrefixBuilder.Build(null, null, null));
        Assert.AreEqual(0, Tokens(PrefixBuilder.Build(null, null, null)));
    }

    [TestMethod]
    public void ThePrefixIsPriceable_WhichIsWhatTheCeilingIsBudgetedAgainst()
    {
        // The reason both routes call Estimate on this string before deciding anything: the
        // ceiling governs the EMBEDDED text, prefix included.
        var prefix = PrefixBuilder.Build("CAO Geestelijke Gezondheidszorg 2024-2026", "ggz",
                                         "Hoofdstuk 3 > 3.2 Onregelmatigheidstoeslag");

        Assert.IsTrue(Tokens(prefix) > 0);
        Assert.IsTrue(Tokens(prefix) < ChunkingBudget.TokenCeiling);
    }

    // Both inverted when ExtractedTextRepair was removed. The prefix is half of EmbeddingText,
    // so what these now pin is that a decomposed character DOES reach the vector: doc.Title
    // falls back to the source id when a document yields no usable title, and nothing repairs
    // it - the 260819 artifact carried "clie" + U+0308 into metadata.Prefix exactly this way.
    [TestMethod]
    public void ADecomposedTitle_ReachesEmbeddedTextAsIs()
    {
        // "clie" + COMBINING DIAERESIS, exactly as the artifact stored it.
        var decomposed = "Folder Beeldzorg - informatie clie\u0308nt -zidw";

        var prefix = PrefixBuilder.Build(decomposed, null, null);

        Assert.IsTrue(prefix.Contains('\u0308'), "nothing composes the prefix any more");
    }

    [TestMethod]
    public void ADecomposedHeadingPath_ReachesEmbeddedTextAsIs()
    {
        var prefix = PrefixBuilder.Build("Hygienecode", null, "Hoofdstuk 3 > Koelen tot 7\u2103");

        Assert.IsTrue(prefix.Contains('\u2103'), "the degree-celsius glyph is no longer folded");
    }

    [TestMethod]
    public void AnAlreadyNormalizedPrefix_IsUnchanged_SoItsVectorIsUnaffected()
    {
        // Building a prefix is deterministic: feeding its own title line back in produces the
        // same string, so a chunk's vector does not shift on a re-run. (This used to be a
        // statement about Repair's idempotence; it holds trivially now that nothing repairs.)
        const string title = "CAO Geestelijke Gezondheidszorg 2024-2026";
        const string path  = "Hoofdstuk 3 > 3.2 Onregelmatigheidstoeslag";

        var once = PrefixBuilder.Build(title, "ggz", path);

        Assert.AreEqual(once, PrefixBuilder.Build(once.Split("\n\n")[0].Replace(" [ggz]", ""), "ggz", path));
    }
}
