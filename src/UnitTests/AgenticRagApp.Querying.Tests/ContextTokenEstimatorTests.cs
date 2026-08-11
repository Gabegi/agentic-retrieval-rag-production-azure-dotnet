using AgenticRagApp.Querying.Services;

namespace RagApp.UnitTests.Querying;

[TestClass]
public class ContextTokenEstimatorTests
{
    [TestMethod]
    public void Estimate_EmptyString_ReturnsZero()
    {
        Assert.AreEqual(0L, ContextTokenEstimator.Estimate(""));
    }

    [TestMethod]
    public void Estimate_DividesLengthByProseCharsPerTokenAndRoundsUp()
    {
        // 10 chars / 3.1 chars-per-token = 3.22... -> ceiling to 4.
        Assert.AreEqual(4L, ContextTokenEstimator.Estimate(new string('a', 10)));
    }

    [TestMethod]
    public void Estimate_ExactMultiple_DoesNotRoundUpUnnecessarily()
    {
        // 62 chars / 3.1 = exactly 20.
        Assert.AreEqual(20L, ContextTokenEstimator.Estimate(new string('a', 62)));
    }

    [TestMethod]
    public void Estimate_LongerContextYieldsProportionallyMoreTokens()
    {
        var shortEstimate = ContextTokenEstimator.Estimate(new string('a', 100));
        var longEstimate  = ContextTokenEstimator.Estimate(new string('a', 1000));

        Assert.IsTrue(longEstimate > shortEstimate);
    }
}
