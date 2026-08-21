using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Services;

using static RagApp.UnitTests.Indexing.ChunkingTestFixtures;

namespace RagApp.UnitTests.Indexing;

// Route 2: nothing trustworthy was declared, so compute a hypothesis. Flat by design - one
// section, N children, no heading machinery at all. An empty heading list is NORMAL input here,
// not a defect: it is this route's whole premise.
//
// chunking-done.md §2 moved the cutting itself into BlockCascade unchanged, so what is tested
// here is what is genuinely this route's own: the whole document is the window, the title line
// is the only context, and the prefix bail.
[TestClass]
public class RecursiveStrategyTests
{
    private static async Task<IReadOnlyList<ChunkObject>> Chunk(PdfExtractionDocument doc) =>
        await new RecursiveStrategy().ChunkDocumentAsync(doc);

    [TestMethod]
    public async Task ADocumentUnderTheCeiling_IsOneFlatChunk()
    {
        var chunks = await Chunk(Doc("Een kort document zonder structuur."));

        Assert.AreEqual(1, chunks.Count);
        Assert.AreEqual(0, chunks[0].SectionIndex);
        Assert.AreEqual(0, chunks[0].ChildIndex);
        Assert.AreEqual(BoundaryLevel.None, chunks[0].BoundaryLevel);
    }

    [TestMethod]
    public async Task EveryChunkCarriesTheDegenerateHeadingConstants()
    {
        // Not a placeholder for heading data that might arrive later - this route never anchors
        // a heading, and a chunk that pretended otherwise would inflate every heading-coverage
        // aggregate the run report produces.
        var chunks = await Chunk(Doc(Sentences(200)));

        Assert.IsTrue(chunks.Count > 1);
        Assert.IsTrue(chunks.All(c => c.SectionIndex == 0), "one section: the document");
        CollectionAssert.AreEqual(
            Enumerable.Range(0, chunks.Count).ToArray(),
            chunks.Select(c => c.ChildIndex).ToArray());

        foreach (var chunk in chunks)
        {
            Assert.IsNull(chunk.HeadingText);
            Assert.IsNull(chunk.HeadingPath);
            Assert.AreEqual(0, chunk.HeadingDepth);
            Assert.AreEqual(ChunkHeadingSource.None, chunk.HeadingSource);
            Assert.IsFalse(chunk.HeadingLocated, "true with source none is a contradiction");
        }
    }

    [TestMethod]
    public async Task TheWholeDocumentIsTheWindow_AndTheCutsCoverItInOrder()
    {
        // This route's "section" IS the document - the guaranteed form of the sparse-giant
        // hazard, and the reason nothing here narrows the range first.
        var doc = Doc(Sentences(200));

        var chunks = await Chunk(doc);

        foreach (var chunk in chunks)
            Assert.AreEqual(doc.Content.Substring(chunk.Start, chunk.Length), chunk.Content);

        for (var i = 1; i < chunks.Count; i++)
            Assert.IsTrue(chunks[i].Start >= chunks[i - 1].Start + chunks[i - 1].Length);
    }

    [TestMethod]
    public async Task AtomicBlocksAreRespectedHereToo_BecauseTheCascadeIsShared()
    {
        // The whole point of the extraction: an oversized section and an unstructured document
        // cannot drift apart about what a table is.
        var table = "| Functie | Schaal |\n| --- | --- |\n" + string.Join("\n",
            Enumerable.Range(0, 200).Select(i => "| Rol " + i + " | FWG " + i + " |"));

        Assert.IsTrue(Tokens(table) > ChunkingBudget.TokenCeiling, "the fixture has to exceed the ceiling to be cut at all");

        var chunks = await Chunk(Doc(table));

        Assert.IsTrue(chunks.Count > 1);
        Assert.IsTrue(chunks.All(c => c.BoundaryLevel == BoundaryLevel.TableRow));
    }

    [TestMethod]
    public async Task APrefixCostingMoreThanTheBodyFloor_BailsRatherThanEmittingChunksThatAreMostlyTitle()
    {
        // The title line plus the sector tag is the ONLY context a chunk on this route carries -
        // there is no heading path to add - which is why an oversized title is worth reporting
        // rather than absorbing. Past MinBodyTokenBudget the prefix is not context any more, it
        // is the chunk.
        var giantTitle = Prose(400);
        Assert.IsTrue(Tokens(ChunkingHelperTitleLine(giantTitle)) > ChunkingBudget.MinBodyTokenBudget,
            "the fixture has to actually breach the floor for this test to mean anything");

        var chunks = await Chunk(Doc(Sentences(100), title: giantTitle));

        Assert.AreEqual(0, chunks.Count);
    }

    [TestMethod]
    public async Task ATitleUnderTheFloor_DoesNotBail()
    {
        // The other side of the same threshold, so a broken comparison cannot pass both.
        var chunks = await Chunk(Doc(Sentences(100), title: "CAO GGZ 2024 2026", domainTag: "ggz"));

        Assert.IsTrue(chunks.Count > 0);
    }

    [TestMethod]
    public async Task BlankContent_ProducesNoChunks()
    {
        Assert.AreEqual(0, (await Chunk(Doc(""))).Count);
        Assert.AreEqual(0, (await Chunk(Doc("   \n\n  "))).Count);
    }

    [TestMethod]
    public async Task ANullFamily_IsNormalInput()
    {
        // Identity resolution may produce no family for a document; the route still runs.
        var chunks = await Chunk(Doc(Prose(30), title: "Losse handleiding", domainTag: null));

        Assert.IsTrue(chunks.Count > 0);
    }

    [TestMethod]
    public void TheRouteNamesItself_BecauseStep4StampsThatName()
    {
        Assert.AreEqual("Recursive", new RecursiveStrategy().Name);
    }

    // The prefix the route actually prices, built the same way the strategy builds it.
    private static string ChunkingHelperTitleLine(string title) =>
        PrefixBuilder.Build(title, null, headingPath: null);
}
