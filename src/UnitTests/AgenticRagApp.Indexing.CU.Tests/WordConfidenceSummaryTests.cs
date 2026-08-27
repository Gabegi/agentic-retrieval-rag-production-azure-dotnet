using AgenticRagApp.Indexing.CU.Models;

namespace RagApp.UnitTests.PdfExtraction;

// The read-quality summary (2026-08-27). What is worth pinning is the shape of the answer, not
// the arithmetic: that "no measurement" stays distinguishable from "confidence zero", and that
// the percentile convention matches DocumentRowBuilder's - two reports whose percentiles are
// computed differently cannot be read against each other, which is the whole reason this exists.
[TestClass]
public class WordConfidenceSummaryTests
{
    [TestMethod]
    public void From_ReturnsNull_WhenNoWordCarriedAConfidence()
    {
        // A document whose response reported no word confidences has not been measured. Zero
        // would claim the service read every word with no confidence at all, which is a
        // different and much worse fact.
        Assert.IsNull(WordConfidenceSummary.From([]));
    }

    [TestMethod]
    public void From_SummarisesTheDistribution()
    {
        // 20 values, 0.05 .. 1.00. Nearest-rank: P5 -> ceil(0.05*20)-1 = index 0, P50 -> index 9.
        var confidences = Enumerable.Range(1, 20).Select(i => i / 20.0).ToList();

        var summary = WordConfidenceSummary.From(confidences)!;

        Assert.AreEqual(20,   summary.WordCount);
        Assert.AreEqual(0.05, summary.Min,  1e-9);
        Assert.AreEqual(0.05, summary.P5,   1e-9);
        Assert.AreEqual(0.50, summary.P50,  1e-9);
        Assert.AreEqual(0.525, summary.Mean, 1e-9);
    }

    [TestMethod]
    public void From_SortsBeforeSummarising_SoInputOrderCannotChangeTheAnswer()
    {
        // Words arrive in page order, not confidence order. A percentile read off unsorted input
        // would silently depend on which page a bad word happened to be on.
        var ascending  = WordConfidenceSummary.From([0.1, 0.4, 0.9])!;
        var descending = WordConfidenceSummary.From([0.9, 0.4, 0.1])!;

        Assert.AreEqual(ascending.Min, descending.Min, 1e-9);
        Assert.AreEqual(ascending.P50, descending.P50, 1e-9);
        Assert.AreEqual(0.1, descending.Min, 1e-9);
    }

    [TestMethod]
    public void From_HandlesASingleWord_WithoutIndexingOffTheEnd()
    {
        // ceil(0.05*1)-1 = 0 and ceil(0.5*1)-1 = 0; the clamp is what keeps a one-word document
        // from throwing rather than reporting.
        var summary = WordConfidenceSummary.From([0.73])!;

        Assert.AreEqual(1, summary.WordCount);
        Assert.AreEqual(0.73, summary.Min,  1e-9);
        Assert.AreEqual(0.73, summary.P5,   1e-9);
        Assert.AreEqual(0.73, summary.P50,  1e-9);
        Assert.AreEqual(0.73, summary.Mean, 1e-9);
    }
}
