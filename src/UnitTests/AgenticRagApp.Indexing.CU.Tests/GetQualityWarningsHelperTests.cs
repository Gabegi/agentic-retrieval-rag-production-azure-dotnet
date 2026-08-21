using Microsoft.VisualStudio.TestTools.UnitTesting;
using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Services;

namespace RagApp.UnitTests.PdfExtraction;

[TestClass]
public class GetQualityWarningsHelperTests
{
    private static Heading MakeHeading(string content) => new(content, "sectionHeading", 0, 1);

    // --- OrphanedNumberedHeading (post-merge residue) -------------------------

    [TestMethod]
    public void ResidualBareLabelHeading_ProducesOrphanedNumberedHeadingWarning()
    {
        var headings = new[] { MakeHeading("Artikel 5") };

        var warnings = GetQualityWarningsHelper.HeadingWarnings(headings, new Dictionary<string, int>(), [], "doc.pdf");

        Assert.IsTrue(warnings.Any(w => w.Code == "OrphanedNumberedHeading" && w.Message!.Contains("1 of 1")));
    }

    [TestMethod]
    public void NormalTitledHeading_ProducesNoOrphanedNumberedHeadingWarning()
    {
        var headings = new[] { MakeHeading("Artikel 1 doel van de opleiding") };

        var warnings = GetQualityWarningsHelper.HeadingWarnings(headings, new Dictionary<string, int>(), [], "doc.pdf");

        Assert.IsFalse(warnings.Any(w => w.Code == "OrphanedNumberedHeading"));
    }

    [TestMethod]
    public void MergedTwoLineHeading_ProducesNoOrphanedNumberedHeadingWarning()
    {
        // The successfully merged shape ("Artikel 9 opleiding") no longer matches
        // the bare-label regex, so it must not be flagged as an orphan.
        var headings = new[] { MakeHeading("Artikel 9 opleiding") };

        var warnings = GetQualityWarningsHelper.HeadingWarnings(headings, new Dictionary<string, int>(), [], "doc.pdf");

        Assert.IsFalse(warnings.Any(w => w.Code == "OrphanedNumberedHeading"));
    }

    [TestMethod]
    public void MultipleOrphans_AggregatedIntoOneWarning_WithCountAndExamples()
    {
        var headings = new[] { MakeHeading("Artikel 5"), MakeHeading("Artikel 1 doel van de opleiding"), MakeHeading("Bijlage IX") };

        var warnings = GetQualityWarningsHelper.HeadingWarnings(headings, new Dictionary<string, int>(), [], "doc.pdf");

        var orphanWarnings = warnings.Where(w => w.Code == "OrphanedNumberedHeading").ToList();
        Assert.AreEqual(1, orphanWarnings.Count);
        Assert.IsTrue(orphanWarnings[0].Message!.Contains("2 of 3"));
        Assert.IsTrue(orphanWarnings[0].Message!.Contains("Artikel 5"));
    }

    [TestMethod]
    public void OrphanedNumberedHeadingWarning_CarriesBlobNameAsTarget()
    {
        var headings = new[] { MakeHeading("Artikel 5") };

        var warnings = GetQualityWarningsHelper.HeadingWarnings(headings, new Dictionary<string, int>(), [], "my-doc.pdf");

        Assert.AreEqual("my-doc.pdf", warnings.Single(w => w.Code == "OrphanedNumberedHeading").Target);
    }

    // --- UnrecognisedNumberedHeadingLabel (pre-merge vocabulary) --------------

    [TestMethod]
    public void UnknownLabel_ProducesUnrecognisedNumberedHeadingLabelWarning()
    {
        var labelsSeen = new Dictionary<string, int> { ["Paragraaf"] = 2 };

        var warnings = GetQualityWarningsHelper.HeadingWarnings([], labelsSeen, [], "doc.pdf");

        Assert.IsTrue(warnings.Any(w => w.Code == "UnrecognisedNumberedHeadingLabel"
                                      && w.Message!.Contains("Paragraaf") && w.Message!.Contains("2 occurrence")));
    }

    [TestMethod]
    [DataRow("Artikel")]
    [DataRow("Hoofdstuk")]
    [DataRow("Bijlage")]
    [DataRow("Article")]
    [DataRow("Chapter")]
    [DataRow("Section")]
    [DataRow("Annex")]
    public void KnownLabel_ProducesNoUnrecognisedNumberedHeadingLabelWarning(string knownLabel)
    {
        var labelsSeen = new Dictionary<string, int> { [knownLabel] = 5 };

        var warnings = GetQualityWarningsHelper.HeadingWarnings([], labelsSeen, [], "doc.pdf");

        Assert.IsFalse(warnings.Any(w => w.Code == "UnrecognisedNumberedHeadingLabel"));
    }

    [TestMethod]
    public void KnownVocabularyCheck_IsCaseInsensitive()
    {
        var labelsSeen = new Dictionary<string, int> { ["ARTIKEL"] = 1 };

        var warnings = GetQualityWarningsHelper.HeadingWarnings([], labelsSeen, [], "doc.pdf");

        Assert.IsFalse(warnings.Any(w => w.Code == "UnrecognisedNumberedHeadingLabel"));
    }

    [TestMethod]
    public void MultipleUnknownLabels_EachProducesItsOwnWarning()
    {
        var labelsSeen = new Dictionary<string, int> { ["Paragraaf"] = 1, ["Rubriek"] = 3 };

        var warnings = GetQualityWarningsHelper.HeadingWarnings([], labelsSeen, [], "doc.pdf");

        var unknownWarnings = warnings.Where(w => w.Code == "UnrecognisedNumberedHeadingLabel").ToList();
        Assert.AreEqual(2, unknownWarnings.Count);
        Assert.IsTrue(unknownWarnings.Any(w => w.Message!.Contains("Paragraaf")));
        Assert.IsTrue(unknownWarnings.Any(w => w.Message!.Contains("Rubriek")));
    }

    // --- Independence of the two warnings, and empty input --------------------

    [TestMethod]
    public void OrphanWithUnknownLabel_ProducesBothWarnings()
    {
        // An unmerged "Paragraaf 9" is both an orphan (post-merge) and unknown
        // vocabulary (pre-merge) - the two signals are independent and both fire.
        var headings = new[] { MakeHeading("Paragraaf 9") };
        var labelsSeen = new Dictionary<string, int> { ["Paragraaf"] = 1 };

        var warnings = GetQualityWarningsHelper.HeadingWarnings(headings, labelsSeen, [], "doc.pdf");

        Assert.IsTrue(warnings.Any(w => w.Code == "OrphanedNumberedHeading"));
        Assert.IsTrue(warnings.Any(w => w.Code == "UnrecognisedNumberedHeadingLabel"));
    }

    [TestMethod]
    public void EmptyHeadingsAndEmptyVocabulary_ProducesNoWarnings()
    {
        var warnings = GetQualityWarningsHelper.HeadingWarnings([], new Dictionary<string, int>(), [], "doc.pdf");

        Assert.AreEqual(0, warnings.Count);
    }

    // --- PairedZeroBodyHeadingsMerged (D2) ------------------------------------

    [TestMethod]
    public void PairedHeadingMerges_ProducesPairedZeroBodyHeadingsMergedWarning()
    {
        var warnings = GetQualityWarningsHelper.HeadingWarnings(
            [], new Dictionary<string, int>(), ["3.3 Wat moet je doen als iets fout gaat?"], "doc.pdf");

        var warning = warnings.Single(w => w.Code == "PairedZeroBodyHeadingsMerged");
        StringAssert.Contains(warning.Message, "1 paired zero-body heading");
        StringAssert.Contains(warning.Message, "3.3 Wat moet je doen als iets fout gaat?");
    }

    [TestMethod]
    public void NoPairedHeadingMerges_ProducesNoPairedZeroBodyHeadingsMergedWarning()
    {
        var warnings = GetQualityWarningsHelper.HeadingWarnings([], new Dictionary<string, int>(), [], "doc.pdf");

        Assert.IsFalse(warnings.Any(w => w.Code == "PairedZeroBodyHeadingsMerged"));
    }
}
