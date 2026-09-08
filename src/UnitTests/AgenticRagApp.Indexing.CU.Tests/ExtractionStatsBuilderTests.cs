using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Services;

namespace RagApp.UnitTests.PdfExtraction;

// The ExtractionStageMetrics row the stage returns. This row travels through Durable Table
// Storage under a 64KB limit, so what it may and may not carry is a decision, not a detail -
// see ExtractionStageMetrics and DocumentSummary.
[TestClass]
public class ExtractionStatsBuilderTests
{
    [TestMethod]
    public void SummariesRideTheMetricsRowAsACountAndAMean_NeverAsText()
    {
        var output = Output(
            new DocumentSummaryEntry("a.pdf", new DocumentSummary("Samenvatting A.", 0.8, 3), true),
            new DocumentSummaryEntry("b.pdf", new DocumentSummary("Samenvatting B.", 0.6, 2), true),
            // No confidence reported: counted as a summary, excluded from the mean rather than
            // dragging it towards zero.
            new DocumentSummaryEntry("c.pdf", new DocumentSummary("Samenvatting C.", null, 0), true));

        var stats = ExtractionStatsBuilder.BuildStats(Diff(output), []);

        Assert.AreEqual(3, stats.SummariesPresent);
        Assert.AreEqual(0.7, stats.SummaryConfidenceMean!.Value, 1e-9);
    }

    [TestMethod]
    public void NoSummaryConfidences_LeaveTheMeanBlank_NotZero()
    {
        var output = Output(
            new DocumentSummaryEntry("a.pdf", new DocumentSummary("Zonder score.", null, 0), true));

        var stats = ExtractionStatsBuilder.BuildStats(Diff(output), []);

        Assert.AreEqual(1, stats.SummariesPresent);
        Assert.IsNull(stats.SummaryConfidenceMean);
    }

    [TestMethod]
    public void ARunThatProducedNoSummariesReportsZeroPresentAndNoMean()
    {
        var stats = ExtractionStatsBuilder.BuildStats(Diff(Output()), []);

        // Zero is the honest answer here, unlike the mean: "no document carried a Summary
        // field" is a measurement, and it is the one worth seeing, since the analyzer bills
        // for generating them either way.
        Assert.AreEqual(0, stats.SummariesPresent);
        Assert.IsNull(stats.SummaryConfidenceMean);
    }

    private static PdfExtractionOutput Output(params DocumentSummaryEntry[] summaries) =>
        new([])
        {
            Issues          = [],
            RedFlags        = [],
            SpotCheckSample = [],
            Summaries       = summaries,
        };

    private static DiffResult Diff(PdfExtractionOutput output) =>
        new("pdf", output, [], [], [], NewCount: 0, Updated: 0, Skipped: 0);
}
