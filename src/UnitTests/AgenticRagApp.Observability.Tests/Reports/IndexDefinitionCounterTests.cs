using AgenticRagApp.Infrastructure.Clients.Search;
using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Observability.Tests.Reports;

// The index-definition run counter (2026-09-15). Decided that day: ALL FIVE vector-definition
// groups reset it - dimensions, vectorizer model, compression, metric, and the HNSW graph
// parameters - which makes it a definition-change counter, not a re-embed-trigger one. Only the
// first three invalidate stored vectors; the other two reset it while costing zero embedding
// calls. These tests pin that deliberately broad behaviour so it cannot be narrowed by accident.
[TestClass]
public class IndexDefinitionCounterTests
{
    private static IndexVectorConfig Config(
        int? dimensions = 3072, string? model = "text-embedding-3-large", string? compression = null,
        string? metric = "cosine", string? algorithm = "Hnsw", int? m = 4, int? efConstruction = 400, int? efSearch = 500) =>
        new(
            IndexName: "idx", FieldName: "content_vector", FieldPresent: true,
            Dimensions: dimensions, ConfiguredDimensions: 3072,
            Algorithm: algorithm, Metric: metric, M: m, EfConstruction: efConstruction, EfSearch: efSearch,
            Compression: compression,
            Vectorizer: "AzureOpenAI", VectorizerModel: model, VectorizerDeployment: "embed",
            ConfiguredModelName: "text-embedding-3-large", ReadAtUtc: DateTimeOffset.UnixEpoch);

    [TestMethod]
    public void SameDefinition_HashesTheSame_AndIsStableAcrossReads()
    {
        // ReadAtUtc and index name are NOT part of the definition - every run would otherwise
        // reset the counter.
        Assert.AreEqual(
            IndexDefinitionCounter.ComputeHash(Config()),
            IndexDefinitionCounter.ComputeHash(Config() with { ReadAtUtc = DateTimeOffset.UtcNow }));
    }

    [TestMethod]
    public void AllFiveGroups_ChangeTheHash()
    {
        var baseline = IndexDefinitionCounter.ComputeHash(Config());

        // 1-3: these invalidate stored vectors.
        Assert.AreNotEqual(baseline, IndexDefinitionCounter.ComputeHash(Config(dimensions: 1536)));
        Assert.AreNotEqual(baseline, IndexDefinitionCounter.ComputeHash(Config(model: "text-embedding-3-small")));
        Assert.AreNotEqual(baseline, IndexDefinitionCounter.ComputeHash(Config(compression: "ScalarQuantization")));
        // 4-5: these do not cost a single embedding call, and still reset the counter - the
        // tradeoff accepted when all five were chosen.
        Assert.AreNotEqual(baseline, IndexDefinitionCounter.ComputeHash(Config(metric: "dotProduct")));
        Assert.AreNotEqual(baseline, IndexDefinitionCounter.ComputeHash(Config(m: 8)));
        Assert.AreNotEqual(baseline, IndexDefinitionCounter.ComputeHash(Config(efConstruction: 800)));
        Assert.AreNotEqual(baseline, IndexDefinitionCounter.ComputeHash(Config(efSearch: 1000)));
    }

    [TestMethod]
    public void NullParameter_HashesDifferentlyFromZero()
    {
        // The service can stop reporting hnswParameters entirely. If null collapsed onto 0 the
        // hash would silently hold still through that change.
        Assert.AreNotEqual(
            IndexDefinitionCounter.ComputeHash(Config(m: null)),
            IndexDefinitionCounter.ComputeHash(Config(m: 0)));
    }

    [TestMethod]
    public void FirstEverRun_StartsAtOne_AndClaimsNoChange()
    {
        // Nothing changed - there was simply no prior hash. That is different from "changed at an
        // unknown time", hence the null timestamp.
        var c = IndexDefinitionCounter.Advance(null, "abc", DateTimeOffset.UnixEpoch);

        Assert.AreEqual(1, c.RunsSinceDefinitionChange);
        Assert.IsFalse(c.DefinitionChangedThisRun);
        Assert.IsNull(c.DefinitionChangedAtUtc);
        Assert.IsNull(c.PreviousDefinitionHash);
    }

    [TestMethod]
    public void UnchangedDefinition_Increments_AndKeepsTheOriginalChangeTimestamp()
    {
        var changedAt = DateTimeOffset.UnixEpoch;
        var first  = IndexDefinitionCounter.Advance(null, "abc", changedAt);
        var second = IndexDefinitionCounter.Advance(first, "abc", changedAt.AddDays(1));
        var third  = IndexDefinitionCounter.Advance(second, "abc", changedAt.AddDays(2));

        Assert.AreEqual(3, third.RunsSinceDefinitionChange);
        Assert.IsFalse(third.DefinitionChangedThisRun);
        // Still the first run's timestamp: nothing changed on days 1 or 2.
        Assert.IsNull(third.DefinitionChangedAtUtc);
    }

    [TestMethod]
    public void ChangedDefinition_ResetsToOne_AndRecordsWhatItChangedFrom()
    {
        var previous  = IndexDefinitionCounter.Advance(null, "abc", DateTimeOffset.UnixEpoch);
        previous      = IndexDefinitionCounter.Advance(previous, "abc", DateTimeOffset.UnixEpoch);
        var changedAt = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

        var after = IndexDefinitionCounter.Advance(previous, "def", changedAt);

        Assert.AreEqual(1, after.RunsSinceDefinitionChange);
        Assert.IsTrue(after.DefinitionChangedThisRun);
        Assert.AreEqual(changedAt, after.DefinitionChangedAtUtc);
        // Says what it changed FROM, not merely that it changed.
        Assert.AreEqual("abc", after.PreviousDefinitionHash);
        Assert.AreEqual("def", after.DefinitionHash);
    }

    [TestMethod]
    public void ChangedFlag_IsTrueOnlyOnTheRunThatObservedTheChange()
    {
        var changed = IndexDefinitionCounter.Advance(
            IndexDefinitionCounter.Advance(null, "abc", DateTimeOffset.UnixEpoch), "def", DateTimeOffset.UnixEpoch);

        Assert.IsTrue(changed.DefinitionChangedThisRun);
        // The next run on the same definition is comparable again.
        Assert.IsFalse(IndexDefinitionCounter.Advance(changed, "def", DateTimeOffset.UnixEpoch).DefinitionChangedThisRun);
    }
}
