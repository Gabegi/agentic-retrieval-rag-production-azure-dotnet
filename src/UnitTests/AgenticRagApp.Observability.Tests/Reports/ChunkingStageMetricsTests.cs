using AgenticRagApp.Common.Models;
using AgenticRagApp.Observability.Reports;

namespace RagApp.UnitTests.Observability;

[TestClass]
public class ChunkingStageMetricsTests
{
    // Minimal IChunkStatsSource - Observability deliberately never references a pipeline's own
    // chunk type (see IChunkStatsSource), so the tests don't either.
    private sealed record TestChunk(
        string Id, string DocumentId, string Content, string? HeadingText = null,
        int PageStart = 1, int ChildIndex = 0) : IChunkStatsSource
    {
        public bool IsCoherent => Content.Length > 0
            && (char.IsUpper(Content[0]) || char.IsDigit(Content[0]))
            && ".!?:)\"'".Contains(Content[^1]);
    }

    private static TestChunk Chunk(string docId, string content, int index = 0) =>
        new($"{docId}::{index}", docId, content);

    // ── DocsWithZeroChunks ───────────────────────────────────────────────────
    // The regression these guard: allDocIds used to be derived from `chunks` itself, so the
    // set was compared against a subset of itself and DocsWithZeroChunks was structurally
    // always 0 - a document that produced no chunks contributes no chunk to derive its ID from.

    [TestMethod]
    public void Compute_DocumentProducedNoChunks_IsCountedAndNamed()
    {
        var chunks = new[] { Chunk("a.pdf", "Alpha content.") };

        var stats = ChunkingStageMetrics.Compute(chunks, "v1", ["a.pdf", "b.pdf", "c.pdf"]);

        Assert.AreEqual(2, stats.DocsWithZeroChunks);
        CollectionAssert.AreEquivalent(new[] { "b.pdf", "c.pdf" }, stats.ZeroChunkDocumentIds.ToList());
    }

    [TestMethod]
    public void Compute_EveryDocumentProducedChunks_ReportsZero()
    {
        var chunks = new[] { Chunk("a.pdf", "Alpha."), Chunk("b.pdf", "Beta.") };

        var stats = ChunkingStageMetrics.Compute(chunks, "v1", ["a.pdf", "b.pdf"]);

        Assert.AreEqual(0, stats.DocsWithZeroChunks);
        Assert.AreEqual(0, stats.ZeroChunkDocumentIds.Count);
    }

    [TestMethod]
    public void Compute_NoSourceIdsSupplied_ReportsZeroRatherThanGuessing()
    {
        var chunks = new[] { Chunk("a.pdf", "Alpha.") };

        var stats = ChunkingStageMetrics.Compute(chunks, "v1");

        // "Not measured" - the caller gave no input set, so nothing can be concluded.
        Assert.AreEqual(0, stats.DocsWithZeroChunks);
        Assert.AreEqual(0, stats.ZeroChunkDocumentIds.Count);
    }

    [TestMethod]
    public void Compute_NoChunksAtAll_StillNamesEveryInputDocument()
    {
        // The worst case, and the one the old code was least able to report: every document
        // produced nothing, so the whole run indexed nothing.
        var stats = ChunkingStageMetrics.Compute(Array.Empty<TestChunk>(), "v1", ["a.pdf", "b.pdf"]);

        Assert.AreEqual(2, stats.DocsWithZeroChunks);
        CollectionAssert.AreEquivalent(new[] { "a.pdf", "b.pdf" }, stats.ZeroChunkDocumentIds.ToList());
    }

    // ── Samples ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void Compute_CapturesSmallestAndLargestChunks()
    {
        var chunks = new[]
        {
            Chunk("a.pdf", new string('x', 300)),
            Chunk("a.pdf", "tiny", 1),
            Chunk("a.pdf", new string('y', 2000), 2),
        };

        var stats = ChunkingStageMetrics.Compute(chunks, "v1", ["a.pdf"]);

        Assert.IsNotNull(stats.SmallestChunk);
        Assert.IsNotNull(stats.LargestChunk);
        Assert.AreEqual(4,    stats.SmallestChunk!.SizeChars);
        Assert.AreEqual(2000, stats.LargestChunk!.SizeChars);
    }

    [TestMethod]
    public void Compute_TruncatesLongExcerptsAndFlagsThem()
    {
        var chunks = new[] { Chunk("a.pdf", new string('x', 2000)) };

        var stats = ChunkingStageMetrics.Compute(chunks, "v1", ["a.pdf"]);

        Assert.IsTrue(stats.LargestChunk!.Truncated);
        // 500 chars + the ellipsis marker. SizeChars still reports the real length, so a
        // clipped excerpt is never mistaken for a genuinely short chunk.
        Assert.AreEqual(501,  stats.LargestChunk.ContentExcerpt.Length);
        Assert.AreEqual(2000, stats.LargestChunk.SizeChars);
    }

    [TestMethod]
    public void Compute_ShortChunkIsNotMarkedTruncated()
    {
        var chunks = new[] { Chunk("a.pdf", "Short.") };

        var stats = ChunkingStageMetrics.Compute(chunks, "v1", ["a.pdf"]);

        Assert.IsFalse(stats.SmallestChunk!.Truncated);
        Assert.AreEqual("Short.", stats.SmallestChunk.ContentExcerpt);
    }

    [TestMethod]
    public void Compute_SamplesSpreadAcrossSizeBands()
    {
        var chunks = new[]
        {
            Chunk("a.pdf", new string('a', 50)),
            Chunk("a.pdf", new string('b', 300),  1),
            Chunk("a.pdf", new string('c', 900),  2),
            Chunk("a.pdf", new string('d', 2000), 3),
        };

        var stats = ChunkingStageMetrics.Compute(chunks, "v1", ["a.pdf"]);

        // One per band rather than the first N, so the samples represent the distribution.
        Assert.AreEqual(4, stats.SampleChunks.Count);
        CollectionAssert.AreEquivalent(
            new[] { 50, 300, 900, 2000 },
            stats.SampleChunks.Select(s => s.SizeChars).ToList());
    }

    // ── Duplicates ───────────────────────────────────────────────────────────

    [TestMethod]
    public void Compute_IdenticalContent_CountedAndSampledWithOccurrences()
    {
        var chunks = new[]
        {
            Chunk("a.pdf", "Repeated boilerplate."),
            Chunk("b.pdf", "Repeated boilerplate.", 1),
            Chunk("c.pdf", "Repeated boilerplate.", 2),
            Chunk("d.pdf", "Unique content.", 3),
        };

        var stats = ChunkingStageMetrics.Compute(chunks, "v1", ["a.pdf", "b.pdf", "c.pdf", "d.pdf"]);

        // Two *extra* copies beyond the first occurrence.
        Assert.AreEqual(2, stats.DuplicateChunks);
        Assert.AreEqual(1, stats.DuplicateSamples.Count);
        Assert.AreEqual(3, stats.DuplicateSamples[0].Occurrences);
        StringAssert.Contains(stats.DuplicateSamples[0].ContentExcerpt, "Repeated boilerplate.");
        Assert.AreEqual(64, stats.DuplicateSamples[0].ContentHash.Length); // SHA-256 hex
    }

    [TestMethod]
    public void Compute_NoDuplicates_ReportsNoSamples()
    {
        var chunks = new[] { Chunk("a.pdf", "One."), Chunk("b.pdf", "Two.", 1) };

        var stats = ChunkingStageMetrics.Compute(chunks, "v1", ["a.pdf", "b.pdf"]);

        Assert.AreEqual(0, stats.DuplicateChunks);
        Assert.AreEqual(0, stats.DuplicateSamples.Count);
    }

    // ── Existing aggregates still behave ─────────────────────────────────────

    [TestMethod]
    public void Compute_PreservesBandsAndCoherenceCounts()
    {
        // One chunk per band. The 100-500 one is the only coherent chunk: starts with a
        // capital, ends with a full stop.
        var chunks = new[]
        {
            Chunk("a.pdf", new string('a', 50)),
            Chunk("a.pdf", "C" + new string('b', 298) + ".", 1),
            Chunk("a.pdf", new string('c', 900),  2),
            Chunk("a.pdf", new string('d', 2000), 3),
        };

        var stats = ChunkingStageMetrics.Compute(chunks, "v1", ["a.pdf"]);

        Assert.AreEqual(4, stats.ChunksProduced);
        Assert.AreEqual(1, stats.BandUnder100);
        Assert.AreEqual(1, stats.Band100To500);
        Assert.AreEqual(1, stats.Band500To1500);
        Assert.AreEqual(1, stats.Band1500Plus);
        Assert.AreEqual(1, stats.CoherentChunks);
        Assert.AreEqual(50,   stats.MinChunkSizeChars);
        Assert.AreEqual(2000, stats.MaxChunkSizeChars);
    }

    [TestMethod]
    public void Empty_HasNoSamplesAndNoZeroChunkIds()
    {
        var stats = ChunkingStageMetrics.Empty("v1");

        Assert.AreEqual(0, stats.ChunksProduced);
        Assert.AreEqual(0, stats.ZeroChunkDocumentIds.Count);
        Assert.AreEqual(0, stats.SampleChunks.Count);
        Assert.AreEqual(0, stats.DuplicateSamples.Count);
        Assert.IsNull(stats.SmallestChunk);
        Assert.IsNull(stats.LargestChunk);
    }
}
