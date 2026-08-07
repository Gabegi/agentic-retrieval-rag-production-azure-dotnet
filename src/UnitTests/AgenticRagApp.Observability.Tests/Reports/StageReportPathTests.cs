using AgenticRagApp.Observability.Reports;

namespace RagApp.UnitTests.Observability;

[TestClass]
public class StageReportPathTests
{
    private static readonly DateTimeOffset RunAt =
        new(2026, 8, 7, 3, 14, 12, 847, TimeSpan.Zero);

    [TestMethod]
    public void Build_WithInstanceId_KeepsDateFolderAndTimePrefix()
    {
        var path = StageReportPath.Build("indexing/pdf-extraction", RunAt, "a3f9c21b", "validation-report");

        // The instance ID is added to the existing naming, not substituted for it: the date
        // folder still allows browsing to a day, and HHmmssfff still sorts runs chronologically
        // within it.
        Assert.AreEqual(
            "indexing/pdf-extraction/2026/08/07/031412847-a3f9c21b-validation-report.json",
            path);
    }

    [TestMethod]
    public void Build_WithoutInstanceId_FallsBackToTimestampOnlyNaming()
    {
        var path = StageReportPath.Build("indexing/pdf-extraction", RunAt, null, "file-facts");

        Assert.AreEqual("indexing/pdf-extraction/2026/08/07/031412847-file-facts.json", path);
    }

    [TestMethod]
    public void Build_BlankInstanceId_TreatedAsAbsent()
    {
        var path = StageReportPath.Build("indexing/extraction-diff", RunAt, "   ", "diff");

        Assert.AreEqual("indexing/extraction-diff/2026/08/07/031412847-diff.json", path);
    }

    [TestMethod]
    public void Build_DoesNotCollideWithTheRunReportPrefix()
    {
        // The run report lives under runs/, deliberately outside every stage-report folder, so
        // an Event Grid subjectBeginsWith on runs/ cannot match a stage report. See
        // docs/2608/260807/pipeline-run-email-report.md §2.
        var path = StageReportPath.Build("indexing/pdf-extraction", RunAt, "a3f9c21b", "validation-report");

        Assert.IsFalse(path.StartsWith("runs/", StringComparison.Ordinal));
    }
}
