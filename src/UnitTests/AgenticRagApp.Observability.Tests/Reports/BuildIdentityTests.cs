using System.Reflection;
using AgenticRagApp.Observability;
using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Observability.Tests.Reports;

// Which binary produced a run (2026-09-17, D197 action 4). The property that matters is the one
// these pin: two runs of the SAME build produce the same string and two different builds do not.
// Nothing here asserts a particular version number - this repo stamps none today (no Version in
// src/Directory.Build.props, no SourceLink), which is exactly why BuildIdentity carries the MVID
// as well and why a test against "1.0.0" would pin an accident rather than a requirement.
[TestClass]
public class BuildIdentityTests
{
    [TestMethod]
    public void For_IsStableAcrossCallsForTheSameAssembly()
    {
        var assembly = typeof(BuildIdentity).Assembly;

        Assert.AreEqual(BuildIdentity.For(assembly), BuildIdentity.For(assembly));
    }

    // The discriminating half. Two separately-compiled assemblies get different MVIDs, so the
    // string differs even though neither carries a distinguishing version.
    [TestMethod]
    public void For_DiffersBetweenAssemblies()
    {
        Assert.AreNotEqual(
            BuildIdentity.For(typeof(BuildIdentity).Assembly),
            BuildIdentity.For(typeof(BuildIdentityTests).Assembly));
    }

    [TestMethod]
    public void For_CarriesTheModuleVersionId()
    {
        var assembly = typeof(BuildIdentity).Assembly;

        StringAssert.Contains(
            BuildIdentity.For(assembly),
            assembly.ManifestModule.ModuleVersionId.ToString("N"),
            "the MVID is the half that varies per build - without it the string is a constant");
    }

    [TestMethod]
    public void For_CarriesTheInformationalVersionWhenTheAssemblyHasOne()
    {
        var assembly = typeof(BuildIdentity).Assembly;
        var version  = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        Assert.IsFalse(string.IsNullOrWhiteSpace(version), "the SDK emits one even unstamped; if this fails the fallback path needs its own test");
        StringAssert.Contains(BuildIdentity.For(assembly), version!);
    }

    // The field is optional on the report - null means "written before 2026-09-17", same
    // convention as EmptyVectors and RateLimitedRetries - but when the activity sets it, it
    // survives onto the metrics record unchanged.
    [TestMethod]
    public void EmbedUploadStageMetrics_CarriesBuildIdAndTheCacheDivisors()
    {
        var metrics = Metrics() with { };

        Assert.IsNull(metrics.BuildId);
        Assert.IsNull(metrics.MaxCacheParallelism);
        Assert.IsNull(metrics.VectorCacheOperations);

        var stamped = metrics with
        {
            BuildId               = "1.0.0 (deadbeef)",
            MaxCacheParallelism   = 32,
            VectorCacheOperations = 3_988,
        };

        Assert.AreEqual("1.0.0 (deadbeef)", stamped.BuildId);
        Assert.AreEqual(32,    stamped.MaxCacheParallelism);
        Assert.AreEqual(3_988, stamped.VectorCacheOperations);
    }

    private static EmbedUploadStageMetrics Metrics() => new(
        DocsUploaded: 0, DocsFailed: 0, ChunksRemoved: 0, ChunkFamiliesPatched: 0,
        ChunksTruncated: 0, EmbeddingRetries: 0, VectorDimErrors: 0, VectorCacheHits: 0,
        TotalEmbeddingDurationMs: 0, IndexDocumentCountSnapshot: null,
        IndexStorageSizeBytesSnapshot: null, RedFlags: [], ChunksEvicted: 0,
        PreviousIndexDocumentCount: null, PreviousIndexStorageSizeBytes: null);
}
