using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Services;

namespace RagApp.UnitTests.Indexing;

// Route 2's step 8: pieces become ChunkObjects with the degenerate heading constants this
// route always carries. Nothing here is a placeholder for heading data that might arrive
// later - this route never anchors a heading.
[TestClass]
public class FlatChunkBuilderTests
{
    private static ContentPiece Piece(string text, int start, BoundaryLevel level = BoundaryLevel.Line) =>
        new(text, start, text.Length, level);

    [TestMethod]
    public void OneChunkPerPiece_WithARunningChildIndexAndASingleSection()
    {
        // FLAT by definition: there is one section - the document - so SectionIndex is 0 on
        // every chunk and ChildIndex just counts.
        var chunks = FlatChunkBuilder.Build(
            [Piece("eerste", 0), Piece("tweede", 10), Piece("derde", 20)]);

        Assert.AreEqual(3, chunks.Count);
        CollectionAssert.AreEqual(new[] { 0, 0, 0 }, chunks.Select(c => c.SectionIndex).ToArray());
        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, chunks.Select(c => c.ChildIndex).ToArray());
    }

    [TestMethod]
    public void HeadingLocatedIsFalseWithSourceNone_NeverTrue()
    {
        // The strategy this replaced set true with source "none", which reads as a successfully
        // located heading in any aggregate that counts one without reading the other - and so
        // inflates every heading-coverage number the run report produces. Never reproduce it.
        var chunks = FlatChunkBuilder.Build([Piece("tekst", 0)]);

        Assert.IsFalse(chunks[0].HeadingLocated);
        Assert.AreEqual(ChunkHeadingSource.None, chunks[0].HeadingSource);
        Assert.IsNull(chunks[0].HeadingText);
        Assert.IsNull(chunks[0].HeadingPath);
        Assert.AreEqual(0, chunks[0].HeadingDepth);
    }

    [TestMethod]
    public void ContentIsTheBareBody_WithNoPrefixPrependedHere()
    {
        // Step 4 stores the prefix as ChunkMetadata.Prefix and EmbeddingText composes the two.
        // Prepending here would embed the title twice and break the invariant that Content is
        // exactly this chunk's own slice of the source.
        var chunks = FlatChunkBuilder.Build([Piece("De werkgever betaalt.", 40)]);

        Assert.AreEqual("De werkgever betaalt.", chunks[0].Content);
        Assert.AreEqual(40, chunks[0].Start);
        Assert.AreEqual(21, chunks[0].Length);
        Assert.AreEqual("", chunks[0].Metadata.Prefix, "the prefix is step 4's to write, not this builder's");
    }

    [TestMethod]
    public void PieceFlagsAreCarried_NotDefaulted()
    {
        // BoundaryLevel is the fall-through metric and Degraded is the one flag that says an
        // over-ceiling chunk was deliberate; neither can be re-derived downstream.
        var piece = new ContentPiece("| a | 1 |", 5, 9, BoundaryLevel.TableRow, Degraded: true, IsOverlap: true);

        var chunk = FlatChunkBuilder.Build([piece])[0];

        Assert.AreEqual(BoundaryLevel.TableRow, chunk.BoundaryLevel);
        Assert.IsTrue(chunk.Degraded);
        Assert.IsTrue(chunk.IsOverlap);
    }

    [TestMethod]
    public void NoPieces_MeansNoChunks()
    {
        Assert.AreEqual(0, FlatChunkBuilder.Build([]).Count);
    }
}
