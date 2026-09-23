using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Services;

namespace AgenticRagApp.Indexing.CU.Tests;

// D224 A6: the cut-level histogram that replaced CoherentChunks.
[TestClass]
public class CutBoundaryCountersTests
{
    private static ChunkObject Chunk(BoundaryLevel level, string content = "Body.") =>
        new() { Content = content, BoundaryLevel = level };

    [TestMethod]
    public void EveryLevel_IsWritten_InEnumOrder_ZerosIncluded()
    {
        // "HardCut 0" is evidence; an absent key is not. And the order is the ladder's order.
        var (buckets, _) = CutBoundaryCounters.Of([Chunk(BoundaryLevel.None), Chunk(BoundaryLevel.TableRow)]);

        CollectionAssert.AreEqual(
            Enum.GetNames<BoundaryLevel>(), buckets.Keys.ToList());
        Assert.AreEqual(1, buckets["None"]);
        Assert.AreEqual(1, buckets["TableRow"]);
        Assert.AreEqual(0, buckets["HardCut"]);
        Assert.AreEqual(0, buckets["Line"]);
    }

    [TestMethod]
    public void Buckets_SumToTheChunkCount()
    {
        var chunks = new[]
        {
            Chunk(BoundaryLevel.None), Chunk(BoundaryLevel.None), Chunk(BoundaryLevel.Line),
            Chunk(BoundaryLevel.Sentence), Chunk(BoundaryLevel.Word), Chunk(BoundaryLevel.DiagramElement),
        };

        var (buckets, _) = CutBoundaryCounters.Of(chunks);

        Assert.AreEqual(chunks.Length, buckets.Values.Sum());
    }

    [TestMethod]
    public void LineCutsEndingMidSentence_CountsOnlyLinePieces_WithoutASentenceEnder()
    {
        // The seam of a Line cut that did not fall on . ! or ? - the D223 F4 shape. A Sentence
        // piece ending mid-word is not counted: the question is asked of Line cuts only.
        var chunks = new[]
        {
            Chunk(BoundaryLevel.Line,     "dan wordt de zorgverlening daardoor onvrijwillig en moet"),
            Chunk(BoundaryLevel.Line,     "het stappenplan worden gevolgd.\n\n"),
            Chunk(BoundaryLevel.Line,     "De rolverdeling is als volgt:"),
            Chunk(BoundaryLevel.Sentence, "afgebroken zonder punt"),
            Chunk(BoundaryLevel.None,     "Een hele sectie"),
        };

        var (_, midSentence) = CutBoundaryCounters.Of(chunks);

        Assert.AreEqual(2, midSentence);
    }

    [TestMethod]
    public void EmptyInput_StillWritesEveryLevelAsZero()
    {
        var (buckets, midSentence) = CutBoundaryCounters.Of([]);

        Assert.AreEqual(Enum.GetValues<BoundaryLevel>().Length, buckets.Count);
        Assert.IsTrue(buckets.Values.All(v => v == 0));
        Assert.AreEqual(0, midSentence);
    }
}
