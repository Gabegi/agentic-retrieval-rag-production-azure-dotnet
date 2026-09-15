using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Observability.Tests.Reports;

// The cost rollup (2026-09-15). Pure arithmetic over counts other stages already measured -
// no Azure, no clock. What these pin is the null discipline: blank must never become zero,
// because a run whose usage went unreported and a genuinely free run are different facts.
[TestClass]
public class EmbeddingCostMetricsTests
{
    private const decimal Rate = 0.130m;

    [TestMethod]
    public void TokensEmbedded_SumsTheTwoStagesThatBillSeparately()
    {
        // Chunk embeddings bill on the embed stage, identity embeddings on the chunking stage.
        // report-schema.md keeps them apart on purpose; this rollup is the only place they add up.
        var m = EmbeddingCostMetrics.From(
            chunkTokens: 1_000_000, identityTokens: 43_218,
            fullReEmbedTokens: null, prefixTokens: null, prefixShareOfTokens: null,
            rateUsdPer1M: Rate)!;

        Assert.AreEqual(1_043_218L, m.TokensEmbedded);
        Assert.AreEqual(1_000_000L, m.ChunkTokens);
        Assert.AreEqual(43_218L,    m.IdentityTokens);
        // 1,043,218 / 1M x 0.130
        Assert.AreEqual(0.1356m, m.CostUsd);
        // The rate travels with the number it produced, so an old report stays comparable.
        Assert.AreEqual(Rate, m.RateUsdPer1M);
    }

    [TestMethod]
    public void OneStageReportingNothing_DoesNotBlankTheOther()
    {
        // null + 5 is 5, not null: a missing identity measurement must not erase a real chunk one.
        var m = EmbeddingCostMetrics.From(500, null, null, null, null, Rate)!;

        Assert.AreEqual(500L, m.TokensEmbedded);
        Assert.IsNull(m.IdentityTokens);
    }

    [TestMethod]
    public void NeitherStageReporting_IsNullNotZero()
    {
        // A run that reported no usage is not a free run - CostUsd must stay blank.
        var m = EmbeddingCostMetrics.From(null, null, null, null, null, Rate)!;

        Assert.IsNull(m.TokensEmbedded);
        Assert.IsNull(m.CostUsd);
    }

    [TestMethod]
    public void NonPositiveRate_DisablesCostReportingEntirely()
    {
        // Rather than reporting every run as free, which is what a 0 rate would otherwise do.
        Assert.IsNull(EmbeddingCostMetrics.From(1_000_000, 0, null, null, null, 0m));
        Assert.IsNull(EmbeddingCostMetrics.From(1_000_000, 0, null, null, null, -1m));
    }

    [TestMethod]
    public void FullReEmbedAndPrefix_CostedAtTheSameRate()
    {
        var m = EmbeddingCostMetrics.From(
            chunkTokens: 284_542, identityTokens: 0,
            // Every chunk and identity text this run handled, cached or not.
            fullReEmbedTokens: 1_040_000,
            prefixTokens: 219_075, prefixShareOfTokens: 0.21,
            rateUsdPer1M: Rate)!;

        Assert.AreEqual(0.1352m, m.FullReEmbedCostUsd);
        Assert.AreEqual(0.0285m, m.PrefixCostUsd);
        Assert.AreEqual(0.21,    m.PrefixShareOfTokens);
        // The prefix costs tokens and zero index bytes - that asymmetry is the point of
        // reporting its share here and deliberately not in VectorStorageMetrics.
        Assert.IsTrue(m.PrefixCostUsd < m.CostUsd);
    }
}
