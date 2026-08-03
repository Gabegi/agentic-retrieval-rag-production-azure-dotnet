using AgenticRagApp.Common.Models;
using AgenticRagApp.Observability.Reports;

namespace RagApp.UnitTests.Observability;

[TestClass]
public class CsvIndexRunReportTests
{
    private static ExtractionStageMetrics Extraction() => new(
        Source: "csv", DocsToProcess: 5, DocsSkipped: 0, DocsNew: 3, DocsUpdated: 2, DocsDeleted: 0,
        StaleDocumentIds: [], ValidationErrors: 1, ValidationWarnings: 0, ReconciliationProblems: 0,
        StaleDocCount: 1, MojibakeRepairedPages: 0, DetectedTableCount: 0, DocsWithoutHeadings: 0,
        MissingTitleCount: 0, MissingVersionCount: 2, MissingDepartmentCount: 3, TraceabilityGapCount: null,
        Issues: [PipelineIssue.Error(PipelineStage.Clean, "doc1", "boom")],
        RedFlags: ["some flag"],
        SpotCheckSample: [new SpotCheckEntry("doc1", "Title", "preview")]);

    private static CsvIndexRunReport Build() => new()
    {
        Run = new RunIdentity(
            "instance-1",
            DateTimeOffset.Parse("2026-07-24T10:00:00Z"),
            DateTimeOffset.Parse("2026-07-24T10:05:00Z"),
            ForceReindex: false,
            Success: true),
        Extraction             = Extraction(),
        StaleDocCount          = 1,
        MissingVersionCount    = 2,
        MissingDepartmentCount = 3,
    };

    [TestMethod]
    public void Constructor_PropagatesRunIdentityAndCsvSpecificFields()
    {
        var report = Build();

        Assert.AreEqual("instance-1", report.InstanceId);
        Assert.IsTrue(report.Success);
        Assert.AreEqual(1, report.StaleDocCount);
        Assert.AreEqual(2, report.MissingVersionCount);
        Assert.AreEqual(3, report.MissingDepartmentCount);
    }

    [TestMethod]
    public void StageMetrics_AreReachableThroughTheComposedShape()
    {
        var report = Build();

        Assert.AreEqual(5, report.Extraction!.DocsToProcess);
        CollectionAssert.Contains(report.Extraction.RedFlags.ToList(), "some flag");
        Assert.AreEqual(1, report.Extraction.Issues.Count);
        Assert.AreEqual(1, report.Extraction.SpotCheckSample.Count);
    }

    // A stage that never ran is null, not a record full of zeroes - the distinction the
    // old flat shape could not express, since FromResults substituted `?? 0`.
    [TestMethod]
    public void StagesThatNeverRan_AreNull_NotZeroed()
    {
        var report = Build();

        Assert.IsNull(report.Chunking);
        Assert.IsNull(report.Embedding);
    }
}
