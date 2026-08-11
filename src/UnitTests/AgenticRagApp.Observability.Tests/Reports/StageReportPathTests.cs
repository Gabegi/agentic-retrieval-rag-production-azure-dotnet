using AgenticRagApp.Observability.Reports;

namespace RagApp.UnitTests.Observability;

[TestClass]
public class StageReportPathTests
{
    private static readonly DateTimeOffset RunAt =
        new(2026, 8, 7, 3, 14, 12, 847, TimeSpan.Zero);

    [TestMethod]
    public void Build_WithInstanceId_AppendsIdAfterReportName()
    {
        var path = StageReportPath.Build("pdf-validation", RunAt, "a3f9c21b");

        Assert.AreEqual("2026/08/07/20260807T031412847Z-pdf-validation-a3f9c21b.json", path);
    }

    [TestMethod]
    public void Build_WithoutInstanceId_OmitsTrailingIdSegment()
    {
        var path = StageReportPath.Build("pdf-file-facts", RunAt, null);

        Assert.AreEqual("2026/08/07/20260807T031412847Z-pdf-file-facts.json", path);
    }

    [TestMethod]
    public void Build_BlankInstanceId_TreatedAsAbsent()
    {
        var path = StageReportPath.Build("csv-extraction-diff", RunAt, "   ");

        Assert.AreEqual("2026/08/07/20260807T031412847Z-csv-extraction-diff.json", path);
    }
}
