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
        int PageStart = 1, int ChildIndex = 0) : IChunkStatsSource;

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
    public void Compute_PreservesBandCounts()
    {
        // One chunk per band. (Until 2026-09-23 this also asserted CoherentChunks == 1; that
        // metric left the record with D224 A6 and cut quality is CutBoundaries, caller-stamped.)
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
        Assert.IsNull(stats.CutBoundaries, "caller-stamped; Compute must leave it 'not measured'");
        Assert.IsNull(stats.LineCutsEndingMidSentence);
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

    // ── Tokens ───────────────────────────────────────────────────────────────
    // The char fields cannot answer "how full are the chunks against the ceiling" because
    // chars/token is not constant; these pin that the token block comes from the chunks' own
    // stored counts, names the budget it was measured against, and says nothing when there is
    // no count to read.

    private sealed record TokenChunk(
        string Id, string DocumentId, string Content, int? Tokens,
        bool Table = false, int? Prefix = null, int? FigChars = null, int? LogoChars = null) : IChunkStatsSource
    {
        public string? HeadingText                 => null;
        public int     PageStart                   => 1;
        public int     ChildIndex                  => 0;
        public int?    EmbeddedTokenCount          => Tokens;
        public bool    IsTableShaped               => Table;
        public int?    PrefixTokenCount            => Prefix;
        public int?    FigureTextChars             => FigChars;
        public int?    HeaderFooterFigureTextChars => LogoChars;
    }

    private static TokenChunk[] TokenChunks(params int?[] counts) =>
        counts.Select((t, i) => new TokenChunk($"a::{i}", "a.pdf", "body", t)).ToArray();

    private static string WordsOf(int n) => string.Join(' ', Enumerable.Repeat("w", n));

    [TestMethod]
    public void Compute_ChunkTypeWithoutATokenCount_ReportsNoTokenBlock()
    {
        // TestChunk inherits the interface default (null): not measured, so no block - never a
        // block full of zeros.
        var stats = ChunkingStageMetrics.Compute([Chunk("a.pdf", "Alpha.")], "v1", ["a.pdf"], 512, 128);

        Assert.IsNull(stats.Tokens);
    }

    [TestMethod]
    public void Compute_TokenDistribution_ComesFromTheStoredCountsAgainstTheGivenBudget()
    {
        // 10 counts, sorted: 50 100 127 128 200 255 256 400 512 600.
        var chunks = TokenChunks(600, 50, 512, 128, 255, 100, 400, 127, 256, 200);

        var stats = ChunkingStageMetrics.Compute(chunks, "v1", ["a.pdf"], tokenCeiling: 512, minBodyTokenBudget: 128);

        var t = stats.Tokens!;
        Assert.AreEqual(10,    t.Measured);
        Assert.AreEqual(2628L, t.Total);
        Assert.AreEqual(262.8, t.Mean, 0.001);
        Assert.AreEqual(255,   t.P50, "sorted[(int)(10 * 0.50)] = sorted[5]");
        Assert.AreEqual(600,   t.P95, "sorted[(int)(10 * 0.95)] = sorted[9]");
        Assert.AreEqual(600,   t.Max);
        Assert.AreEqual(512,   t.Ceiling);
        Assert.AreEqual(128,   t.MinBodyTokenBudget);
        Assert.AreEqual(1,     t.AboveCeiling,       "strictly over 512: only 600 - 512 itself is at the ceiling, not over it");
        Assert.AreEqual(6,     t.UnderHalfCeiling,   "strictly under 256: 50 100 127 128 200 255");
        Assert.AreEqual(3,     t.UnderMinBodyBudget, "strictly under 128: 50 100 127");
    }

    [TestMethod]
    public void Compute_NoBudgetSupplied_ReportsTheDistributionButNoBudgetRelativeCounts()
    {
        // A caller with no ceiling concept still gets the distribution; the counts that would
        // need a ceiling stay null rather than being measured against a guessed one.
        var stats = ChunkingStageMetrics.Compute(TokenChunks(100, 200, 300), "v1", ["a.pdf"]);

        var t = stats.Tokens!;
        Assert.AreEqual(3,   t.Measured);
        Assert.AreEqual(300, t.Max);
        Assert.IsNull(t.Ceiling);
        Assert.IsNull(t.MinBodyTokenBudget);
        Assert.IsNull(t.AboveCeiling);
        Assert.IsNull(t.UnderHalfCeiling);
        Assert.IsNull(t.UnderMinBodyBudget);
    }

    [TestMethod]
    public void Compute_ChunksWithoutACount_AreLeftOutAndMeasuredSaysSo()
    {
        var stats = ChunkingStageMetrics.Compute(TokenChunks(100, null, 300), "v1", ["a.pdf"], 512, 128);

        Assert.AreEqual(3,    stats.ChunksProduced);
        Assert.AreEqual(2,    stats.Tokens!.Measured, "the gap between the two is visible, not averaged away");
        Assert.AreEqual(400L, stats.Tokens.Total);
    }

    [TestMethod]
    public void Empty_HasNoTokenBlock()
    {
        Assert.IsNull(ChunkingStageMetrics.Empty("v1").Tokens);
        Assert.IsNull(ChunkingStageMetrics.Empty("v1").FigureText);
    }

    // ── Content per token / prefix share ─────────────────────────────────────
    // The ratio is a cost number, not a quality one, and it is not a corpus constant: table
    // markup tokenizes at ~2x prose, so the split has to travel with the overall figure.

    [TestMethod]
    public void Compute_TokensPerWord_OverallAndSplitByTableShape()
    {
        var chunks = new[]
        {
            new TokenChunk("a::0", "a.pdf", WordsOf(4), Tokens: 8),               // prose: 2.0
            new TokenChunk("a::1", "a.pdf", WordsOf(2), Tokens: 8, Table: true),  // table: 4.0
        };

        var t = ChunkingStageMetrics.Compute(chunks, "v1", ["a.pdf"], 512, 128).Tokens!;

        Assert.AreEqual(6L,    t.Words, "whitespace-separated non-empty runs of the measured text");
        Assert.AreEqual(16.0 / 6, t.TokensPerWord, 1e-9);
        Assert.AreEqual(1,     t.TableShapedChunks);
        Assert.AreEqual(4.0,   t.TableTokensPerWord!.Value, 1e-9);
        Assert.AreEqual(2.0,   t.ProseTokensPerWord!.Value, 1e-9);
    }

    [TestMethod]
    public void Compute_NoTableShapedChunks_LeavesTheTableRatioNull()
    {
        var t = ChunkingStageMetrics.Compute([new TokenChunk("a::0", "a.pdf", WordsOf(3), Tokens: 6)], "v1", ["a.pdf"]).Tokens!;

        Assert.IsNull(t.TableTokensPerWord, "an empty population has no ratio - null, not 0");
        Assert.AreEqual(2.0, t.ProseTokensPerWord!.Value, 1e-9);
    }

    [TestMethod]
    public void Compute_PrefixShare_TotalAndMedian()
    {
        // tokens 100/200/300 with prefixes 10/20/60: total share 90/600 = 15%; per-chunk shares
        // 0.10 / 0.10 / 0.20, median sorted[(int)(3 * 0.5)] = 0.10; prefix p50 = 20.
        var chunks = new[]
        {
            new TokenChunk("a::0", "a.pdf", "body", Tokens: 100, Prefix: 10),
            new TokenChunk("a::1", "a.pdf", "body", Tokens: 200, Prefix: 20),
            new TokenChunk("a::2", "a.pdf", "body", Tokens: 300, Prefix: 60),
        };

        var t = ChunkingStageMetrics.Compute(chunks, "v1", ["a.pdf"], 512, 128).Tokens!;

        Assert.AreEqual(20,   t.PrefixTokensP50);
        Assert.AreEqual(90L,  t.PrefixTokensTotal);
        Assert.AreEqual(0.15, t.PrefixShareOfTotal!.Value, 1e-9);
        Assert.AreEqual(0.10, t.PrefixShareP50!.Value, 1e-9);
    }

    [TestMethod]
    public void Compute_NoPrefixCounts_LeavesThePrefixFieldsNull()
    {
        var t = ChunkingStageMetrics.Compute(TokenChunks(100, 200), "v1", ["a.pdf"]).Tokens!;

        Assert.IsNull(t.PrefixTokensP50);
        Assert.IsNull(t.PrefixTokensTotal);
        Assert.IsNull(t.PrefixShareOfTotal);
        Assert.IsNull(t.PrefixShareP50);
    }

    // ── Figure text ──────────────────────────────────────────────────────────
    // D183's 341 / 117 / 23 and its 97 near-empty logo chunks, as a per-run count. Shares are
    // over Content length, cuts at 50% and 90% - D183's own.

    [TestMethod]
    public void Compute_FigureText_CountsChunksAndSharesAtTheTwoCuts()
    {
        var body = new string('x', 100);
        var chunks = new[]
        {
            new TokenChunk("a::0", "a.pdf", body, Tokens: 1, FigChars: 60, LogoChars: 60), // >= 50% figure, >= 50% logo
            new TokenChunk("a::1", "a.pdf", body, Tokens: 1, FigChars: 95, LogoChars: 0),  // >= 90% figure, body figure
            new TokenChunk("a::2", "a.pdf", body, Tokens: 1, FigChars: 0,  LogoChars: 0),  // no figure text
            new TokenChunk("a::3", "a.pdf", body, Tokens: 1),                              // not measured
        };

        var f = ChunkingStageMetrics.Compute(chunks, "v1", ["a.pdf"]).FigureText!;

        Assert.AreEqual(3,    f.Measured, "the unstamped chunk is left out, not counted as zero");
        Assert.AreEqual(2,    f.ChunksWithFigureText);
        Assert.AreEqual(155L, f.FigureTextChars);
        Assert.AreEqual(2,    f.ChunksOver50PctFigureText);
        Assert.AreEqual(1,    f.ChunksOver90PctFigureText);
        Assert.AreEqual(1,    f.ChunksWithHeaderFooterFigureText);
        Assert.AreEqual(60L,  f.HeaderFooterFigureTextChars);
        Assert.AreEqual(1,    f.ChunksOver50PctHeaderFooterFigureText);
        Assert.AreEqual(0,    f.ChunksOver90PctHeaderFooterFigureText);
    }

    [TestMethod]
    public void Compute_NoFigureCounts_ReportsNoFigureBlock()
    {
        Assert.IsNull(ChunkingStageMetrics.Compute(TokenChunks(100), "v1", ["a.pdf"]).FigureText);
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
