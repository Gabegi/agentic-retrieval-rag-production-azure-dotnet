using AgenticRagApp.Indexing.CU.Services;

namespace RagApp.UnitTests.Indexing;

// Classify is the single predicate three sites now share - the cache read pass, the embedder's
// response validation, and (from D199 A1) the upload gate. What it decides, and in what ORDER it
// decides it, is therefore load-bearing in three places at once, so it is pinned here directly
// rather than only through the callers that consume it.
[TestClass]
public class VectorHealthTests
{
    private const int Dims = 4;

    [TestMethod]
    public void Classify_RightWidthWithContent_IsHealthy()
    {
        Assert.AreEqual(VectorVerdict.Healthy, VectorHealth.Classify([0.1f, 0f, 0f, -0.2f], Dims));
    }

    [TestMethod]
    public void Classify_WrongWidth_IsWrongWidth()
    {
        Assert.AreEqual(VectorVerdict.WrongWidth, VectorHealth.Classify([0.1f, 0.2f], Dims));
    }

    [TestMethod]
    public void Classify_RightWidthAllZero_IsEmpty()
    {
        Assert.AreEqual(VectorVerdict.Empty, VectorHealth.Classify([0f, 0f, 0f, 0f], Dims));
    }

    [TestMethod]
    [DataRow(float.NaN,              "NaN")]
    [DataRow(float.PositiveInfinity, "+Infinity")]
    [DataRow(float.NegativeInfinity, "-Infinity")]
    public void Classify_RightWidthWithNonFiniteComponent_IsNonFinite(float component, string label)
    {
        Assert.AreEqual(VectorVerdict.NonFinite, VectorHealth.Classify([0.1f, component, 0.3f, 0.4f], Dims), label);
    }

    // NonFinite beats Empty, and this is the case that makes the precedence observable: every
    // other component is zero, so the vector qualifies as Empty too. It must not read as Empty -
    // all-zero uploads cleanly and merely fails to match, whereas this one cannot be sent at all
    // (D199 C1), and the log line that tells them apart is chosen by this verdict.
    [TestMethod]
    public void Classify_AllZeroExceptOneNonFinite_IsNonFinite()
    {
        Assert.AreEqual(VectorVerdict.NonFinite, VectorHealth.Classify([0f, 0f, float.NaN, 0f], Dims));
    }

    // And width still beats NonFinite, for the same reason it beats Empty.
    [TestMethod]
    public void Classify_WrongWidthWithNonFiniteComponent_IsWrongWidth()
    {
        Assert.AreEqual(VectorVerdict.WrongWidth, VectorHealth.Classify([float.NaN, 0.2f], Dims));
    }

    // The case that proves the precedence is doing work rather than describing an impossibility.
    // A zero-length vector satisfies BOTH conditions - it is vacuously all-zero, since
    // no component is non-zero - so the only thing deciding the answer is that Classify asks
    // about width first. Reporting it as Empty would say "the model returned a useless vector"
    // about what is really a shape mismatch, and would point whoever reads the flag at the
    // deployment instead of at the dimension config.
    [TestMethod]
    public void Classify_ZeroLength_IsWrongWidth_NotEmpty()
    {
        Assert.AreEqual(VectorVerdict.WrongWidth, VectorHealth.Classify([], Dims));
    }

    // The other both-defects case: wrong width AND all-zero. Same precedence, same reason.
    [TestMethod]
    public void Classify_WrongWidthAndAllZero_IsWrongWidth()
    {
        Assert.AreEqual(VectorVerdict.WrongWidth, VectorHealth.Classify([0f, 0f], Dims));
    }

    // Zero expected dimensions is not a case the pipeline can produce (the config defaults to
    // 3,072 and the index is built from the same value), but Classify is public and the answer
    // should not be an accident: with both at zero the width matches, and the vacuous truth then
    // makes it Empty rather than Healthy.
    [TestMethod]
    public void Classify_ZeroLengthAgainstZeroExpected_IsEmpty()
    {
        Assert.AreEqual(VectorVerdict.Empty, VectorHealth.Classify([], 0));
    }

    [TestMethod]
    public void Classify_Null_Throws()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => VectorHealth.Classify(null!, Dims));
    }
}
