using AgenticRagApp.Indexing.CU.Services;
using AgenticRagApp.Indexing.CU.Utils;

namespace RagApp.UnitTests.Indexing;

// Inputs are built from explicit \u escapes rather than literals, so no editor or encoding
// pass can silently precompose them and turn a test into a tautology.
[TestClass]
public class ExtractedTextRepairTests
{
    // "e" followed by U+0308 COMBINING DIAERESIS - the decomposed spelling of "ë".
    private const string DecomposedE = "e\u0308";

    [TestMethod]
    public void DecomposedDiacritics_AreComposedToNfc()
    {
        // One letter, two byte sequences. The 260818 index carried 508 of the decomposed
        // form in heading fields - every one an exact-match miss.
        Assert.AreEqual("Hygiënecode", ExtractedTextRepair.Repair($"Hygi{DecomposedE}necode"));
    }

    [TestMethod]
    public void DegreeCelsiusGlyph_IsFoldedToSearchableForm()
    {
        // U+2103 is NFC-stable, so without the explicit fold the corpus said "℃" 788 times
        // and "°C" never - unmatchable by anyone typing a temperature.
        Assert.AreEqual("Bewaar onder 7 °C.", ExtractedTextRepair.Repair("Bewaar onder 7 ℃."));
    }

    [TestMethod]
    public void LigaturesInvisiblesAndNbsp_AreRepairedLikeThePageBody()
    {
        Assert.AreEqual("fiets", ExtractedTextRepair.Repair("ﬁets"));           // ﬁ
        Assert.AreEqual("ab", ExtractedTextRepair.Repair("a\u200B\u00ADb"));   // zero-width, soft hyphen
        Assert.AreEqual("a b", ExtractedTextRepair.Repair("a\u00A0b"));         // NBSP
    }

    [TestMethod]
    public void HeadingFlatten_RunsTheRepair()
    {
        // Flatten is the single funnel heading_text and heading_path flow through - the
        // repair has to ride on it or DI's raw headings reach the index unnormalized.
        Assert.AreEqual("Hygiënecode voor zorginstellingen",
            HeadingTextNormalizer.Flatten($"Hygi{DecomposedE}necode voor\nzorginstellingen"));
    }

    [TestMethod]
    public void GetTitle_RunsTheRepair()
    {
        // Titles come from native metadata or the blob name, neither of which passes through
        // PdfCleaner - and the title is the first line of every chunk's embedded text.
        Assert.AreEqual("Hygiënecode 2023",
            GetTitleHelper.GetTitle(null, $"Hygi{DecomposedE}necode 2023.pdf"));
    }
}
