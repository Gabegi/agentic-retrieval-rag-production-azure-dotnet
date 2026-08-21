using Microsoft.VisualStudio.TestTools.UnitTesting;
using AgenticRagApp.Indexing.Csv.Services;

namespace RagApp.UnitTests.CsvExtraction;

[TestClass]
public class ChunkingStrategy1Tests
{
    [TestMethod]
    public void ShortContent_FitsInMaxChars_ReturnsSingleChunk()
    {
        var strategy = new ChunkingStrategy1(maxChars: 1_500, overlapChars: 150);

        var chunks = strategy.Chunk("A short sentence.");

        Assert.AreEqual(1, chunks.Count);
        Assert.AreEqual("A short sentence.", chunks[0].Content);
        Assert.AreEqual(0, chunks[0].Index);
    }

    [TestMethod]
    public void EmptyContent_ProducesNoChunks()
    {
        var strategy = new ChunkingStrategy1();

        var chunks = strategy.Chunk("");

        Assert.AreEqual(0, chunks.Count);
    }

    [TestMethod]
    public void WhitespaceOnlyContent_ProducesNoChunks()
    {
        var strategy = new ChunkingStrategy1(maxChars: 10, overlapChars: 2);

        var chunks = strategy.Chunk("   ");

        Assert.AreEqual(0, chunks.Count);
    }

    [TestMethod]
    public void ContentLongerThanMaxChars_SplitsIntoMultipleChunks()
    {
        var strategy = new ChunkingStrategy1(maxChars: 50, overlapChars: 10);
        var content  = string.Concat(Enumerable.Repeat("This is a sentence. ", 10)); // 200 chars

        var chunks = strategy.Chunk(content);

        Assert.IsTrue(chunks.Count > 1);
        CollectionAssert.AreEqual(Enumerable.Range(0, chunks.Count).ToList(), chunks.Select(c => c.Index).ToList());
    }

    [TestMethod]
    public void SplitPrefersSentenceBoundary_ChunkEndsOnPunctuation()
    {
        var content = "Sentence one is here. Sentence two follows now. Sentence three trails after that.";
        var strategy = new ChunkingStrategy1(maxChars: 30, overlapChars: 5);

        var chunks = strategy.Chunk(content);

        Assert.IsTrue(chunks[0].Content.EndsWith('.'), $"Expected first chunk to end on a sentence boundary, got: '{chunks[0].Content}'");
    }

    [TestMethod]
    public void NoSentenceOrWordBoundary_HardSplitsAtMaxChars()
    {
        var content  = new string('a', 300);
        var strategy = new ChunkingStrategy1(maxChars: 100, overlapChars: 10);

        var chunks = strategy.Chunk(content);

        Assert.IsTrue(chunks.Count > 1);
        Assert.IsTrue(chunks.All(c => c.Content.Length <= 100));
    }

    [TestMethod]
    public void OverlapChars_AdjacentChunksShareTrailingContext()
    {
        var content = "First sentence goes here. Second sentence goes here. Third sentence goes here. Fourth sentence goes here.";
        var strategy = new ChunkingStrategy1(maxChars: 55, overlapChars: 20);

        var chunks = strategy.Chunk(content);

        Assert.IsTrue(chunks.Count >= 2);
        var reconstructed = string.Join(" ", chunks.Select(c => c.Content));
        Assert.IsTrue(reconstructed.Contains("First sentence"));
        Assert.IsTrue(reconstructed.Contains("Fourth sentence"));
    }

    [TestMethod]
    public void ZeroOverlap_StillTerminatesAndCoversAllContent()
    {
        var content  = string.Concat(Enumerable.Repeat("Word ", 100)); // 500 chars, no punctuation
        var strategy = new ChunkingStrategy1(maxChars: 60, overlapChars: 0);

        var chunks = strategy.Chunk(content);

        Assert.IsTrue(chunks.Count > 1);
        Assert.IsTrue(chunks.Last().Content.TrimEnd().EndsWith("Word"));
    }

    [TestMethod]
    public void Name_IsSentenceAwareSlidingWindow()
    {
        var strategy = new ChunkingStrategy1();

        Assert.AreEqual("SentenceAwareSlidingWindow", strategy.Name);
    }
}
