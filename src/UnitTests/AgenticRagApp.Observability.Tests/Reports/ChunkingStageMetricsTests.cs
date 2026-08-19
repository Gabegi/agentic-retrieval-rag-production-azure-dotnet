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

    // ── StatsText ────────────────────────────────────────────────────────────
    // The PDF pipeline's Content stopped being prefix + body and became the bare body, so the
    // size bands and the duplicate check moved onto StatsText. These pin both halves of that:
    // a chunk type that does not override it is measured exactly as before, and one that does
    // is measured on what it overrode it with.

    // A chunk whose indexed body and embedded text differ, as ChunkObject's do.
    private sealed record PrefixedChunk(
        string Id, string DocumentId, string Content, string Prefix) : IChunkStatsSource
    {
        public string? HeadingText => null;
        public int     PageStart   => 1;
        public int     ChildIndex  => 0;
        public bool    IsCoherent  => false;

        public string StatsText => $"{Prefix}\n\n{Content}";
    }

    [TestMethod]
    public void Compute_ChunkWithoutAnOverride_MeasuresContentExactlyAsBefore()
    {
        var chunks = new[] { Chunk("a.pdf", new string('a', 300)) };

        var stats = ChunkingStageMetrics.Compute(chunks, "v1", ["a.pdf"]);

        Assert.AreEqual(300, stats.MaxChunkSizeChars);
        Assert.AreEqual(1,   stats.Band100To500);
    }

    [TestMethod]
    public void Compute_ChunkWithAnOverride_MeasuresTheOverriddenText()
    {
        // 90-char body, 12-char prefix plus the joiner: the body alone is in the under-100 band
        // and the embedded string is not. Which band it lands in is the whole point.
        var chunks = new[]
        {
            new PrefixedChunk("a::0", "a.pdf", new string('a', 90), "CAO GGZ [ggz]"),
        };

        var stats = ChunkingStageMetrics.Compute(chunks, "v1", ["a.pdf"]);

        Assert.AreEqual(0, stats.BandUnder100, "the bare body would have landed here");
        Assert.AreEqual(1, stats.Band100To500);
        Assert.AreEqual(105, stats.MaxChunkSizeChars, "90 body + 13 prefix + 2 joiner");
        Assert.AreEqual(105, stats.SmallestChunk!.SizeChars,
            "the sample's size and its excerpt have to describe the same string");
    }

    [TestMethod]
    public void Compute_IdenticalBodiesUnderDifferentPrefixes_AreNotDuplicates()
    {
        // The sharp case: two sections with the same body text under different headings. On
        // Content alone these collapse into a duplicate pair, which is a measurement artefact
        // of the prefix split rather than anything the chunker did.
        var body   = new string('a', 200);
        var chunks = new[]
        {
            new PrefixedChunk("a::0", "a.pdf", body, "CAO GGZ [ggz] > Artikel 1"),
            new PrefixedChunk("a::1", "a.pdf", body, "CAO GGZ [ggz] > Artikel 2"),
        };

        var stats = ChunkingStageMetrics.Compute(chunks, "v1", ["a.pdf"]);

        Assert.AreEqual(0, stats.DuplicateChunks);
        Assert.AreEqual(0, stats.DuplicateSamples.Count);
    }

    [TestMethod]
    public void Compute_IdenticalBodiesUnderTheSamePrefix_AreStillDuplicates()
    {
        var body   = new string('a', 200);
        var chunks = new[]
        {
            new PrefixedChunk("a::0", "a.pdf", body, "CAO GGZ [ggz] > Artikel 1"),
            new PrefixedChunk("a::1", "a.pdf", body, "CAO GGZ [ggz] > Artikel 1"),
        };

        var stats = ChunkingStageMetrics.Compute(chunks, "v1", ["a.pdf"]);

        Assert.AreEqual(1, stats.DuplicateChunks);
        Assert.AreEqual(2, stats.DuplicateSamples.Single().Occurrences);
    }

    // ── ResidueChunksDropped ─────────────────────────────────────────────────

    [TestMethod]
    public void Compute_DoesNotInventAResidueCount()
    {
        // Compute cannot see dropped chunks - they are gone from the list before it runs - so it
        // reports 0 and leaves the count to the caller that did the dropping.
        var stats = ChunkingStageMetrics.Compute([Chunk("a.pdf", "Alpha.")], "v1", ["a.pdf"]);

        Assert.AreEqual(0, stats.ResidueChunksDropped);
    }

    [TestMethod]
    public void ResidueChunksDropped_SurvivesTheCallersWithExpression()
    {
        var stats = ChunkingStageMetrics.Compute([Chunk("a.pdf", "Alpha.")], "v1", ["a.pdf"])
                    with { ResidueChunksDropped = 7 };

        Assert.AreEqual(7, stats.ResidueChunksDropped);
        Assert.AreEqual(1, stats.ChunksProduced, "the rest of the record is untouched");
    }
}
