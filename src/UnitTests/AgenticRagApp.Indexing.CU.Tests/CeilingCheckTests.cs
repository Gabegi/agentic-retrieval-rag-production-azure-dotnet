using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Services;

using static RagApp.UnitTests.Indexing.ChunkingTestFixtures;

namespace RagApp.UnitTests.Indexing;

// The ladder's stop condition. Two lines of production code, and one of them is the reason
// the cascade cannot silently lose text.
[TestClass]
public class CeilingCheckTests
{
    private static ContentPiece Piece(string text) => new(text, 0, text.Length, BoundaryLevel.Line);

    [TestMethod]
    public void EmptyResult_DoesNotFit()
    {
        // A cut level that produced nothing has not succeeded - it has LOST the text. Reporting
        // true here would end the cascade with zero pieces, indistinguishable from a block that
        // genuinely had no content. Falling through costs one wasted level and reaches HardCut,
        // which always produces something.
        Assert.IsFalse(CeilingCheck.AllFit([], 512));
    }

    [TestMethod]
    public void ExactlyAtTheCeiling_Fits()
    {
        // On the boundary on purpose: the comparison is <=, and an off-by-one here would send
        // every ceiling-sized piece down a rung it does not need.
        var text = Prose(20);

        Assert.IsTrue(CeilingCheck.AllFit([Piece(text)], Tokens(text)));
    }

    [TestMethod]
    public void OneTokenOverTheCeiling_DoesNotFit()
    {
        var text = Prose(20);

        Assert.IsFalse(CeilingCheck.AllFit([Piece(text)], Tokens(text) - 1));
    }

    [TestMethod]
    public void OneOversizedPieceAmongFittingOnes_FailsTheWholeLevel()
    {
        // All, not any: a level is accepted only if every piece it produced fits, because the
        // caller's next move is to keep them all.
        var small = Prose(3);
        var big   = Prose(400);

        Assert.IsFalse(CeilingCheck.AllFit([Piece(small), Piece(big), Piece(small)], Tokens(small) + 1));
    }
}
