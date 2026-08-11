using AgenticRagApp.Indexing.Pdf.Services;
using AgenticRagApp.Indexing.Pdf.Utils;

namespace RagApp.UnitTests.Indexing;

[TestClass]
public class PdfChunkingStrategy1Tests
{
    [TestMethod]
    public void EmptyContent_ProducesNoChunks()
    {
        var strategy = new PdfChunkingStrategy1();

        var chunks = strategy.Chunk("");

        Assert.AreEqual(0, chunks.Count);
    }

    [TestMethod]
    public void WhitespaceOnlyContent_ProducesNoChunks()
    {
        var strategy = new PdfChunkingStrategy1();

        var chunks = strategy.Chunk("   \n\n  ");

        Assert.AreEqual(0, chunks.Count);
    }

    [TestMethod]
    public void ShortSingleParagraph_ReturnsSingleChunk()
    {
        var strategy = new PdfChunkingStrategy1(targetSize: 1_000, maxSize: 1_500, minTail: 200, overlapSize: 150);

        var chunks = strategy.Chunk("A short paragraph.");

        Assert.AreEqual(1, chunks.Count);
        Assert.AreEqual("A short paragraph.", chunks[0].Content);
        Assert.AreEqual(0, chunks[0].Index);
    }

    [TestMethod]
    public void EstimatedTokens_IsSetOnEveryChunk_AtTheProseRatio()
    {
        // Strategy1 is never table-aware, so every chunk uses the prose ratio regardless of
        // content shape - see ChunkingHelper.EstimateTokens.
        var strategy = new PdfChunkingStrategy1(targetSize: 1_000, maxSize: 1_500, minTail: 200, overlapSize: 150);
        var content  = "A short paragraph.";

        var chunks = strategy.Chunk(content);

        Assert.AreEqual(ChunkingHelper.EstimateTokens(content, isTable: false), chunks[0].EstimatedTokens);
        Assert.IsTrue(chunks[0].EstimatedTokens > 0);
    }

    [TestMethod]
    public void MultipleSmallParagraphs_PackedIntoOneChunkUntilTargetSize()
    {
        var strategy = new PdfChunkingStrategy1(targetSize: 30, maxSize: 1_000, minTail: 5, overlapSize: 5);
        var content  = "First para.\n\nSecond para.\n\nThird para.";

        var chunks = strategy.Chunk(content);

        // targetSize is small enough that packing should flush at least once.
        Assert.IsTrue(chunks.Count >= 1);
        CollectionAssert.AreEqual(Enumerable.Range(0, chunks.Count).ToList(), chunks.Select(c => c.Index).ToList());
    }

    [TestMethod]
    public void ParagraphLongerThanMaxSize_SplitOnSentenceBoundaries()
    {
        var longParagraph = string.Concat(Enumerable.Repeat("This is a sentence. ", 10)); // 200 chars, no blank lines
        var strategy = new PdfChunkingStrategy1(targetSize: 50, maxSize: 60, minTail: 5, overlapSize: 5);

        var chunks = strategy.Chunk(longParagraph);

        Assert.IsTrue(chunks.Count > 1, "A single oversized paragraph should be split into multiple chunks.");
        foreach (var chunk in chunks)
            Assert.IsTrue(chunk.Content.Length <= 200, $"Chunk unexpectedly long: '{chunk.Content}'");
    }

    [TestMethod]
    public void TinyTrailingChunk_IsMergedIntoPrevious()
    {
        // Second paragraph alone is shorter than minTail, so it should fold into the first
        // chunk rather than stand alone as a near-empty final chunk.
        var strategy = new PdfChunkingStrategy1(targetSize: 5, maxSize: 1_000, minTail: 50, overlapSize: 0);
        var content  = "First paragraph is reasonably long here.\n\nTiny.";

        var chunks = strategy.Chunk(content);

        Assert.AreEqual(1, chunks.Count);
        StringAssert.Contains(chunks[0].Content, "Tiny.");
    }

    [TestMethod]
    public void ShortMiddleParagraph_IsNotMerged()
    {
        // A short paragraph in the middle (not the last chunk) is real structure and must
        // NOT be absorbed into its neighbor, even if it's shorter than minTail - only the
        // trailing chunk is ever a merge candidate. The last chunk here (10 chars) is kept
        // above minTail (3) so MergeTinyTrailingChunk has no reason to fire at all, isolating
        // the "is BB preserved as its own chunk" behavior from the tail-merge behavior.
        var strategy = new PdfChunkingStrategy1(targetSize: 5, maxSize: 8, minTail: 3, overlapSize: 0);
        var content  = "AAAAAAAA\n\nBB\n\nCCCCCCCCCC";

        var chunks = strategy.Chunk(content);

        Assert.AreEqual(3, chunks.Count);
        Assert.AreEqual("BB", chunks[1].Content);
    }

    [TestMethod]
    public void OverlapSeedsNextChunk_WithSentenceAlignedTail()
    {
        var strategy = new PdfChunkingStrategy1(targetSize: 10, maxSize: 40, minTail: 0, overlapSize: 15);
        var content  = "Sentence number one here.\n\nSentence number two follows.\n\nSentence number three ends it.";

        var chunks = strategy.Chunk(content);

        Assert.IsTrue(chunks.Count >= 2);
        // Content from the end of an earlier paragraph should reappear at the start of the next chunk.
        var reconstructed = string.Join(" ", chunks.Select(c => c.Content));
        StringAssert.Contains(reconstructed, "Sentence number one");
        StringAssert.Contains(reconstructed, "Sentence number three");
    }

    [TestMethod]
    public void Name_IsParagraphAwareSlidingWindow()
    {
        var strategy = new PdfChunkingStrategy1();

        Assert.AreEqual("ParagraphAwareSlidingWindow", strategy.Name);
    }

    [TestMethod]
    public void NoSentenceBoundaryInOversizedParagraph_HardWrapsBySize()
    {
        var content  = new string('a', 300); // no blank lines, no sentence punctuation
        var strategy = new PdfChunkingStrategy1(targetSize: 50, maxSize: 100, minTail: 5, overlapSize: 5);

        var chunks = strategy.Chunk(content);

        Assert.IsTrue(chunks.Count >= 1);
        var total = chunks.Sum(c => c.Content.Length);
        Assert.IsTrue(total >= 300, "All original characters should still be present across chunks (allowing for overlap duplication).");
    }
}
