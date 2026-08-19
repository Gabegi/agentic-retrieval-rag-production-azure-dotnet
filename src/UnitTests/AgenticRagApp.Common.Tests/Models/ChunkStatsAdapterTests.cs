using AgenticRagApp.Common.Models;

namespace RagApp.UnitTests.Common;

// StatsText is the property ChunkingStageMetrics measures sizes and duplicates on. Both pipelines
// override it with their EmbeddingText because both hold text that is embedded but not stored in
// Content - PDF the prefix, CSV the summary. The default on the interface is Content, so an
// override that goes missing does not fail: it silently measures the wrong string and reports a
// band and a duplicate count that look perfectly reasonable.
[TestClass]
public class ChunkStatsAdapterTests
{
    private static ChunkStatsAdapter Chunk(string content, string? summary = null) =>
        new() { Id = "c1", DocumentId = "d1", Content = content, Summary = summary };

    [TestMethod]
    public void StatsTextFoldsInTheSummary_BecauseTheEmbeddingDoes()
    {
        var chunk = Chunk("Body text.", "Curated summary.");

        Assert.AreEqual(chunk.EmbeddingText, chunk.StatsText);
        Assert.AreEqual("Curated summary.\n\nBody text.", chunk.StatsText);
        Assert.AreNotEqual(chunk.Content, chunk.StatsText,
            "if these ever match with a summary present, the override has been lost");
    }

    [TestMethod]
    public void WithNoSummary_StatsTextIsJustTheContent()
    {
        var chunk = Chunk("Body text.");

        Assert.AreEqual("Body text.", chunk.StatsText);
    }

    [TestMethod]
    public void TwoChunksWithTheSameBodyUnderDifferentSummaries_AreNotTheSameStatsText()
    {
        // The duplicate-detection case: these produce different vectors, so counting them as
        // duplicates of each other is a measurement artifact rather than a finding.
        var a = Chunk("Identical body.", "Summary one.");
        var b = Chunk("Identical body.", "Summary two.");

        Assert.AreEqual(a.Content, b.Content);
        Assert.AreNotEqual(a.StatsText, b.StatsText);
    }
}
