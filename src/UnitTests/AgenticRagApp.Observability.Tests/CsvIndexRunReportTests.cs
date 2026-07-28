using AgenticRagApp.Observability.Reports;

namespace RagApp.UnitTests.Observability;

[TestClass]
public class CsvIndexRunReportTests
{
    private static CsvIndexRunReport Build() => new()
    {
        InstanceId              = "instance-1",
        StartedAt               = DateTimeOffset.Parse("2026-07-24T10:00:00Z"),
        FinishedAt              = DateTimeOffset.Parse("2026-07-24T10:05:00Z"),
        ForceReindex             = false,
        Success                  = true,
        ErrorMessage             = null,
        DocsToProcess            = 5,
        DocsNew                  = 3,
        DocsUpdated              = 2,
        StaleDocCount            = 1,
        MissingVersionCount      = 2,
        MissingDepartmentCount   = 3,
        Issues                   = [new ValidationIssueEntry("Clean", "Error", "doc1", "boom")],
        RedFlags                 = ["some flag"],
        SpotCheckSample          = [new SpotCheckEntry("doc1", "Title", "preview")],
    };

    [TestMethod]
    public void Constructor_PropagatesBaseAndCsvSpecificFields()
    {
        var report = Build();

        Assert.AreEqual("instance-1", report.InstanceId);
        Assert.IsTrue(report.Success);
        Assert.AreEqual(5, report.DocsToProcess);
        Assert.AreEqual(1, report.StaleDocCount);
        Assert.AreEqual(2, report.MissingVersionCount);
        Assert.AreEqual(3, report.MissingDepartmentCount);
        CollectionAssert.Contains(report.RedFlags.ToList(), "some flag");
        Assert.AreEqual(1, report.Issues.Count);
        Assert.AreEqual(1, report.SpotCheckSample.Count);
    }

    [TestMethod]
    public void IsAssignableTo_IndexRunReportBase()
    {
        IndexRunReportBase report = Build();

        Assert.AreEqual("instance-1", report.InstanceId);
    }
}
