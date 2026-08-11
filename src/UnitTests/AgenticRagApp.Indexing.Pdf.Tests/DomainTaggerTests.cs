using AgenticRagApp.Indexing.Pdf.Utils;

namespace RagApp.UnitTests.Indexing;

[TestClass]
public class DomainTaggerTests
{
    [TestMethod]
    public void Tag_TitleWithNoKnownSectorCode_ReturnsNull()
    {
        Assert.IsNull(DomainTagger.Tag("Aanbrengbonus (Versie 5)"));
    }

    [TestMethod]
    public void Tag_EmptyOrWhitespaceTitle_ReturnsNull()
    {
        Assert.IsNull(DomainTagger.Tag(""));
        Assert.IsNull(DomainTagger.Tag("   "));
    }

    [TestMethod]
    [DataRow("CAO GGZ (Versie 4)", "GGZ")]
    [DataRow("CAO GHZ (Versie 4)", "GHZ")]
    [DataRow("CAO VVT (Versie 6)", "VVT")]
    [DataRow("Brochure verstrekkingen GGZ_VGZ (Versie 1)", "GGZ")]
    public void Tag_TitleWithKnownSectorCode_ReturnsIt(string title, string expected)
    {
        Assert.AreEqual(expected, DomainTagger.Tag(title));
    }

    [TestMethod]
    [DataRow("Brochure verstrekkingen V&V (Versie 3)")]
    [DataRow("Brochure verstrekkingen V & V (Versie 3)")]
    public void Tag_VenVTitle_IsCanonicalisedToVvt(string title)
    {
        // V&V and VVT are the same sector - one canonical tag, or retrieval fragments.
        Assert.AreEqual("VVT", DomainTagger.Tag(title));
    }

    [TestMethod]
    public void Tag_IsCaseInsensitive()
    {
        Assert.AreEqual("GGZ", DomainTagger.Tag("cao ggz (versie 4)"));
    }

    [TestMethod]
    public void Tag_GhzRequiresUppercase_SoTheFrequencyUnitDoesNotMatch()
    {
        Assert.IsNull(DomainTagger.Tag("Handleiding 2.4 GHz koppeling"));
        Assert.AreEqual("GHZ", DomainTagger.Tag("CAO GHZ 2024"));
    }

    [TestMethod]
    public void Tag_DoesNotMatchSubstringInsideALongerWord()
    {
        // "GGZet" shouldn't match GGZ - the code is anchored to whole-word boundaries.
        Assert.IsNull(DomainTagger.Tag("GGZet Beleid"));
    }

    [TestMethod]
    public void Tag_TreatsDiacriticsAsLetters()
    {
        // \p{L} rather than [A-Za-z], so an accented neighbour is still "inside a word".
        Assert.IsNull(DomainTagger.Tag("GGZé Beleid"));
        Assert.IsNull(DomainTagger.Tag("éGGZ Beleid"));
    }

    [TestMethod]
    public void Tag_VenVN_IsNotASectorMatch()
    {
        // V&VN is the professional association, not the sector.
        Assert.IsNull(DomainTagger.Tag("Beroepsprofiel V&VN"));
    }

    [TestMethod]
    public void TagAll_TitleNamingTwoSectors_ReturnsBothInOrder()
    {
        CollectionAssert.AreEqual(
            new[] { "GGZ", "VGZ" },
            DomainTagger.TagAll("Brochure verstrekkingen GGZ_VGZ (Versie 1)").ToArray());
    }

    [TestMethod]
    public void TagAll_TitleNamingBothVvtAndVenV_ReturnsVvtOnce()
    {
        CollectionAssert.AreEqual(
            new[] { "VVT" },
            DomainTagger.TagAll("CAO VVT / V&V").ToArray());
    }

    [TestMethod]
    public void Tag_TitleNamingTwoSectors_ResolvesByPatternsOrderNotTitleOrder()
    {
        Assert.AreEqual("GGZ", DomainTagger.Tag("Vergelijking GHZ en GGZ"));
    }

    [TestMethod]
    public void TagAll_NoKnownSectorCode_ReturnsEmpty()
    {
        Assert.AreEqual(0, DomainTagger.TagAll("Aanbrengbonus (Versie 5)").Count);
        Assert.AreEqual(0, DomainTagger.TagAll("   ").Count);
    }
}
