using AgenticRagApp.Observability.Reports;

namespace RagApp.UnitTests.Observability;

// The report's stand-in for the per-op histogram nobody on this pipeline can read (D203 §6c).
// Nearest-rank, so every figure is a sample that occurred; null for no samples, never a zero.
[TestClass]
public class LatencySummaryTests
{
    [TestMethod]
    public void From_NoSamples_IsNull()
    {
        Assert.IsNull(LatencySummary.From([]));
    }

    [TestMethod]
    public void From_OneSample_EveryFigureIsThatSample()
    {
        var s = LatencySummary.From([42.0])!;

        Assert.AreEqual(1, s.Count);
        Assert.AreEqual(42.0, s.P50Ms);
        Assert.AreEqual(42.0, s.P95Ms);
        Assert.AreEqual(42.0, s.MaxMs);
    }

    // Twenty samples 1..20: nearest-rank P50 is the 10th (ceil(0.5 × 20) = 10), P95 the 19th
    // (ceil(0.95 × 20) = 19), max the 20th. Order of input must not matter.
    [TestMethod]
    public void From_TwentySamples_NearestRankPercentiles()
    {
        var samples = Enumerable.Range(1, 20).Select(i => (double)i).Reverse().ToArray();

        var s = LatencySummary.From(samples)!;

        Assert.AreEqual(20,   s.Count);
        Assert.AreEqual(10.0, s.P50Ms);
        Assert.AreEqual(19.0, s.P95Ms);
        Assert.AreEqual(20.0, s.MaxMs);
    }

    [TestMethod]
    public void From_RoundsToOneDecimal()
    {
        var s = LatencySummary.From([34.5678])!;

        Assert.AreEqual(34.6, s.P50Ms);
    }
}
