using Microsoft.VisualStudio.TestTools.UnitTesting;
using AgenticRagApp.Indexing.Pdf.Services;
using AgenticRagApp.Indexing.Pdf.Utils;

namespace RagApp.UnitTests.PdfExtraction;

// Covers PdfChunkingStrategy2 - the LIVE table-aware chunking strategy registered in
// ServiceCollectionExtensions (not the legacy sibling ChunkingStrategy2, already covered
// by ChunkingStrategy2Tests.cs). Exercised only through the public Chunk(...) entry point,
// which is how SplitIntoBlocks and ChunkTable are reached in production.
[TestClass]
public class PdfChunkingStrategy2Tests
{
    [TestMethod]
    public void EmptyOrWhitespaceContent_ProducesNoChunks()
    {
        var strategy = new PdfChunkingStrategy2();

        Assert.AreEqual(0, strategy.Chunk("").Count);
        Assert.AreEqual(0, strategy.Chunk("   \n  ").Count);
    }

    [TestMethod]
    public void SmallTable_FitsInOneChunk_IsNeverSplit()
    {
        var table = "| Name | Dose |\n|---|---|\n| Aspirin | 100mg |\n| Ibuprofen | 200mg |";
        var strategy = new PdfChunkingStrategy2(maxSize: 1500);

        var chunks = strategy.Chunk(table);

        Assert.AreEqual(1, chunks.Count);
        Assert.AreEqual(table, chunks[0].Content);
    }

    [TestMethod]
    public void TableChunk_EstimatedTokens_UsesTableRatio_ProseChunk_UsesProseRatio()
    {
        var table = "| Name | Dose |\n|---|---|\n| Aspirin | 100mg |\n| Ibuprofen | 200mg |";
        var text  = "This is sentence one. This is sentence two. This is sentence three.";
        var strategy = new PdfChunkingStrategy2(maxSize: 1500);

        var tableChunks = strategy.Chunk(table);
        var proseChunks = strategy.Chunk(text);

        Assert.AreEqual(ChunkingHelper.EstimateTokens(table, isTable: true), tableChunks[0].EstimatedTokens);
        Assert.AreEqual(ChunkingHelper.EstimateTokens(text, isTable: false), proseChunks[0].EstimatedTokens);
    }

    [TestMethod]
    public void OversizedTable_SplitsRowsAndRepeatsHeaderAndSeparatorOnEveryChunk()
    {
        var table =
            "| Name | Dose |\n" +
            "|---|---|\n" +
            "| Aspirin | 100mg |\n" +
            "| Ibuprofen | 200mg |\n" +
            "| Paracetamol | 500mg |\n" +
            "| Amoxicillin | 250mg |";
        var strategy = new PdfChunkingStrategy2(maxSize: 45); // forces multiple row-groups

        var chunks = strategy.Chunk(table);

        Assert.IsTrue(chunks.Count > 1, "expected the table to split into more than one chunk");
        foreach (var chunk in chunks)
        {
            StringAssert.StartsWith(chunk.Content, "| Name | Dose |");
            StringAssert.Contains(chunk.Content, "|---|---|");
        }
        var combined = string.Join("\n", chunks.Select(c => c.Content));
        StringAssert.Contains(combined, "Aspirin");
        StringAssert.Contains(combined, "Ibuprofen");
        StringAssert.Contains(combined, "Paracetamol");
        StringAssert.Contains(combined, "Amoxicillin");
    }

    [TestMethod]
    public void TableWithNoSeparatorRow_TreatsFirstLineAloneAsHeader()
    {
        // No "|---|---|" row: LooksLikeSeparatorRow(lines[1]) is false, so headerCount=1
        // and every data row (including what would have been the separator) is repeated.
        var table =
            "| Name | Dose |\n" +
            "| Aspirin | 100mg long enough to force a split here today |\n" +
            "| Ibuprofen | 200mg long enough to force a split here too |";
        var strategy = new PdfChunkingStrategy2(maxSize: 40);

        var chunks = strategy.Chunk(table);

        Assert.IsTrue(chunks.Count > 1);
        foreach (var chunk in chunks)
            StringAssert.StartsWith(chunk.Content, "| Name | Dose |");
    }

    [TestMethod]
    public void SingleDataRowLargerThanMaxChars_IsKeptIntactRatherThanHardSplit()
    {
        var hugeRow = "| " + new string('x', 200) + " | y |";
        var table = "| Name | Dose |\n|---|---|\n" + hugeRow;
        var strategy = new PdfChunkingStrategy2(maxSize: 50);

        var chunks = strategy.Chunk(table);

        Assert.IsTrue(chunks.Any(c => c.Content.Contains(hugeRow)),
            "an oversized single row must survive whole, not be cut mid-row");
    }

    [TestMethod]
    public void ProseOnlyContent_ChunksTheSameAsUnderlyingStrategy1()
    {
        var text = "This is sentence one. This is sentence two. This is sentence three.";
        var strategy = new PdfChunkingStrategy2(maxSize: 1500);

        var chunks = strategy.Chunk(text);

        Assert.AreEqual(1, chunks.Count);
        Assert.AreEqual(text, chunks[0].Content);
    }

    [TestMethod]
    public void ProseContainingALonePipeCharacter_IsNotMisclassifiedAsTable()
    {
        // SplitIntoBlocks demotes a single matching line back to prose - a real table
        // needs at least 2 consecutive lines matching the table-row shape.
        var text = "The cost is |20 depending on the flavor selected today.";
        var strategy = new PdfChunkingStrategy2(maxSize: 1500);

        var chunks = strategy.Chunk(text);

        Assert.AreEqual(1, chunks.Count);
        Assert.AreEqual(text, chunks[0].Content);
    }

    [TestMethod]
    public void TableSurroundedByProse_TableStaysIntactAsItsOwnChunk()
    {
        var content =
            "Some introductory prose about dosing.\n\n" +
            "| Name | Dose |\n|---|---|\n| Aspirin | 100mg |\n\n" +
            "Some concluding remarks about the table above.";
        var strategy = new PdfChunkingStrategy2(maxSize: 1500);

        var chunks = strategy.Chunk(content);

        Assert.IsTrue(chunks.Any(c => c.Content.Contains("| Name | Dose |") && c.Content.Contains("Aspirin")));
        Assert.IsTrue(chunks.Any(c => c.Content.Contains("introductory")));
        Assert.IsTrue(chunks.Any(c => c.Content.Contains("concluding")));
    }

    [TestMethod]
    public void TwoConsecutiveTableBlocks_SeparatedByProse_AreChunkedIndependently()
    {
        var content =
            "| A | B |\n|---|---|\n| 1 | 2 |\n\n" +
            "Prose in between.\n\n" +
            "| C | D |\n|---|---|\n| 3 | 4 |";
        var strategy = new PdfChunkingStrategy2(maxSize: 1500);

        var chunks = strategy.Chunk(content);

        Assert.IsTrue(chunks.Any(c => c.Content.Contains("| A | B |") && c.Content.Contains("1")));
        Assert.IsTrue(chunks.Any(c => c.Content.Contains("| C | D |") && c.Content.Contains("3")));
        Assert.IsTrue(chunks.Any(c => c.Content.Contains("Prose in between")));
    }

    [TestMethod]
    public void LoneTableRowShapedLine_IsDemotedAndMergedWithSurroundingProse()
    {
        // A "table-row-shaped" line followed by a blank line (not another table-row line)
        // never accumulates 2 consecutive matches, so it's never promoted to a table block,
        // and the demoted line merges back into one prose block with what follows.
        var content = "| just one line |\n\nMore text below.";
        var strategy = new PdfChunkingStrategy2(maxSize: 1500);

        var chunks = strategy.Chunk(content);

        Assert.AreEqual(1, chunks.Count);
        StringAssert.Contains(chunks[0].Content, "| just one line |");
        StringAssert.Contains(chunks[0].Content, "More text below.");
    }
}
