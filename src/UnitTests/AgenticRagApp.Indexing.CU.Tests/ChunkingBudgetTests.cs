using AgenticRagApp.Indexing.CU.Services;

namespace RagApp.UnitTests.Indexing;

// Two constants, pinned - because the reason they were hoisted into one place is that two
// copies had already drifted. The ceiling previously lived on the old SectionSplitter, the run
// report read it from there, and the strategies each carried their own private 512.
//
// A row that says "chunks above ceiling" has to mean the same ceiling the cut was budgeted
// against; changing either number here is a corpus-wide re-chunk and a full re-embed, which is
// what these assertions make deliberate rather than incidental.
[TestClass]
public class ChunkingBudgetTests
{
    [TestMethod]
    public void TokenCeilingIs512_TheEmbeddingModelsStartingPoint()
    {
        // Governs the EMBEDDED text, prefix included - which is why both routes price the
        // prefix BEFORE cutting rather than appending it after.
        Assert.AreEqual(512, ChunkingBudget.TokenCeiling);
    }

    [TestMethod]
    public void MinBodyTokenBudgetIs128_TheFloorTheBodyKeeps()
    {
        // Below this a chunk stops being worth retrieving at all, so the ceiling is breached by
        // choice instead - which is what Degraded records.
        Assert.AreEqual(128, ChunkingBudget.MinBodyTokenBudget);
    }

    [TestMethod]
    public void TheFloorLeavesRoomUnderTheCeiling_ForAPrefixToBePaidFor()
    {
        // The relationship, not just the values: if the floor ever reached the ceiling, the
        // Math.Max in both strategies would stop being a floor and start being the answer.
        Assert.IsTrue(ChunkingBudget.MinBodyTokenBudget < ChunkingBudget.TokenCeiling);
    }
}
