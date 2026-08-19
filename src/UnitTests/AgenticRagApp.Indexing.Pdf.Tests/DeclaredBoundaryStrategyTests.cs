using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Indexing.Pdf.Services;
using AgenticRagApp.Indexing.Pdf.Utils;

using static RagApp.UnitTests.Indexing.ChunkingTestFixtures;

namespace RagApp.UnitTests.Indexing;

// Route 1: the document declared its units - honour them.
//
// The over-ceiling branch is what chunking-done.md §2 landed. Before it, the strategy ended at
// "// Over the ceiling - TO DO." and a section that did not fit was SILENTLY DROPPED, which
// made the route's chunk count a floor rather than a total. Route 1's own measurement says
// 13-17% of sections need a cut, so that was not a rare path.
[TestClass]
public class DeclaredBoundaryStrategyTests
{
    private static async Task<IReadOnlyList<ChunkObject>> Chunk(PdfExtractionDocument doc) =>
        await new DeclaredBoundaryStrategy().ChunkDocumentAsync(doc);

    // Content laid out as N sections of the given body, with the section bounds computed from
    // the same string the document carries - so the bounds are true by construction rather than
    // by arithmetic done twice.
    private static (PdfExtractionDocument Doc, List<LocatedSection> Sections) Document(
        IReadOnlyList<string> bodies, string title = "", string? domainTag = null, string? headingPath = "Artikel 1")
    {
        var content  = string.Join("\n\n", bodies);
        var sections = new List<LocatedSection>();
        var at       = 0;

        for (var i = 0; i < bodies.Count; i++)
        {
            var end = i + 1 < bodies.Count ? at + bodies[i].Length + 2 : content.Length;
            sections.Add(Section(i, at, end, headingText: "Artikel " + (i + 1), headingPath: headingPath));
            at = end;
        }

        return (Doc(content, title, domainTag, sections), sections);
    }

    // ── the fit gate ─────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ASectionUnderItsBodyCeiling_IsKeptWholeAsOneChunk()
    {
        // The 83-87% path. Start and Length are the SECTION's own, so the slice invariant holds
        // by construction - the body was sliced at exactly these bounds.
        var (doc, sections) = Document([Prose(20), Prose(20)]);

        var chunks = await Chunk(doc);

        Assert.AreEqual(2, chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            Assert.AreEqual(BoundaryLevel.None, chunks[i].BoundaryLevel);
            Assert.AreEqual(sections[i].Start,  chunks[i].Start);
            Assert.AreEqual(sections[i].Length, chunks[i].Length);
            Assert.AreEqual(doc.Content.Substring(chunks[i].Start, chunks[i].Length), chunks[i].Content);
        }
    }

    [TestMethod]
    public async Task EverySectionProducesAtLeastOneChunk_IncludingTheOversizedOnes()
    {
        // The regression the over-ceiling branch fixes: an oversized section used to vanish, so
        // the route reported fewer chunks than it had sections and nothing said why.
        var (doc, _) = Document([Prose(20), Sentences(120), Prose(20)]);

        var chunks = await Chunk(doc);

        CollectionAssert.AreEquivalent(
            new[] { 0, 1, 2 },
            chunks.Select(c => c.SectionIndex).Distinct().ToArray());
    }

    // ── the over-ceiling branch ──────────────────────────────────────────────

    [TestMethod]
    public async Task AnOversizedSection_IsCutByTheCascade_NotDropped()
    {
        var (doc, sections) = Document([Sentences(200)]);

        var chunks = await Chunk(doc);

        Assert.IsTrue(chunks.Count > 1, "an oversized section is cut, not emitted whole");
        Assert.IsTrue(chunks.All(c => c.Start >= sections[0].Start && c.Start + c.Length <= sections[0].End),
            "no cut escaped the section window");
    }

    [TestMethod]
    public async Task CutsFromAnOversizedSection_StillAddressTheDocument()
    {
        // The coordinate contract, at the level route 1 actually uses it: BlockCascade is
        // windowed to [section.Start, section.End) and returns pieces in doc.Content
        // coordinates, so the slice invariant survives the narrowing.
        var (doc, _) = Document([Prose(20), Sentences(200)]);

        foreach (var chunk in await Chunk(doc))
            Assert.AreEqual(doc.Content.Substring(chunk.Start, chunk.Length), chunk.Content);
    }

    [TestMethod]
    public async Task EveryChildOfAnOversizedSection_KeepsThatSectionsHeading()
    {
        // The section keeps its boundary and its heading; only its BODY is cut. Bailing the
        // whole document to the recursive route instead would discard every heading in a
        // document because one section ran long.
        var (doc, _) = Document([Sentences(200)]);

        var chunks = await Chunk(doc);

        Assert.IsTrue(chunks.Count > 1);
        Assert.IsTrue(chunks.All(c => c.HeadingText == "Artikel 1"));
        Assert.IsTrue(chunks.All(c => c.HeadingLocated));
        Assert.IsTrue(chunks.All(c => c.HeadingSource == ChunkHeadingSource.DiHeading));
    }

    [TestMethod]
    public async Task ChildIndexRestartsPerSection_WhileSectionIndexTracksTheSection()
    {
        // The reason chunks are built per section rather than pooled: a document-wide piece list
        // cannot say which section a piece came from, and that pair is the chunk's identity.
        var (doc, _) = Document([Sentences(200), Prose(20), Sentences(200)]);

        var chunks = await Chunk(doc);

        foreach (var group in chunks.GroupBy(c => c.SectionIndex))
            CollectionAssert.AreEqual(
                Enumerable.Range(0, group.Count()).ToArray(),
                group.Select(c => c.ChildIndex).ToArray(),
                "section " + group.Key + " does not number its children from 0");
    }

    [TestMethod]
    public async Task CutsAreOrderedAndDisjointAcrossTheWholeDocument()
    {
        var (doc, _) = Document([Sentences(120), Prose(20), Sentences(120)]);

        var chunks = await Chunk(doc);

        for (var i = 1; i < chunks.Count; i++)
            Assert.IsTrue(chunks[i].Start >= chunks[i - 1].Start + chunks[i - 1].Length,
                "chunk " + i + " overlaps its predecessor");
    }

    // ── prefix pricing ───────────────────────────────────────────────────────

    [TestMethod]
    public async Task TheSameSectionCutsIntoMorePieces_WhenThePrefixCostsMore()
    {
        // The prefix goes INSIDE the embedded text, so it is priced BEFORE the cut - adding it
        // afterwards would change every vector and force a full re-embed. A more expensive
        // prefix leaves less body budget, and the cut has to show it.
        var body = Sentences(300);

        var cheap = await Chunk(Document([body]).Doc);

        var expensive = await Chunk(Document(
            [body],
            title: "CAO Geestelijke Gezondheidszorg 2024 2026 inclusief alle bijlagen en protocollen",
            domainTag: "ggz",
            headingPath: "Deel A > Hoofdstuk 3 > Paragraaf 3.2 Onregelmatigheidstoeslag").Doc);

        Assert.IsTrue(expensive.Count > cheap.Count,
            "priced prefix " + cheap.Count + " vs " + expensive.Count);
    }

    [TestMethod]
    public async Task TheBodyFloorHolds_SoAnExtremePrefixCannotPriceTheBodyToNothing()
    {
        // ChunkingBudget.MinBodyTokenBudget is the floor the body keeps no matter how expensive
        // the prefix got. Below it a chunk stops being worth retrieving at all, so the ceiling
        // is breached BY CHOICE instead.
        var (doc, _) = Document(
            [Sentences(200)],
            title: Prose(400),
            domainTag: "ggz",
            headingPath: string.Join(" > ", Enumerable.Range(0, 8).Select(i => Prose(20, "niveau" + i))));

        var chunks = await Chunk(doc);

        Assert.IsTrue(chunks.Count > 0, "the section is still cut rather than abandoned");
        Assert.IsTrue(chunks.Any(c => Tokens(c.Content) > 1),
            "the body budget never collapses to nothing - it stops at the floor");
    }

    [TestMethod]
    public async Task TheEmbeddedPrefixIsNotPrependedIntoContent()
    {
        // Content is the BARE BODY on both routes. Step 4 stores the prefix separately and
        // EmbeddingText composes the two.
        var (doc, _) = Document([Prose(20)], title: "CAO GGZ", domainTag: "ggz");

        var chunks = await Chunk(doc);

        Assert.IsFalse(chunks[0].Content.Contains("CAO GGZ"));
        Assert.IsFalse(chunks[0].Content.Contains("[ggz]"));
    }

    // ── degenerate input ─────────────────────────────────────────────────────

    [TestMethod]
    public async Task NoSections_MeansNothingToHonour_AndSoNoChunks()
    {
        // Empty is a routing mistake, not a defect here: the gate promised declared structure.
        Assert.AreEqual(0, (await Chunk(Doc("wat tekst", sections: []))).Count);
        Assert.AreEqual(0, (await Chunk(Doc("wat tekst"))).Count, "null LocatedSections means the same");
    }

    [TestMethod]
    public async Task BlankContent_ProducesNoChunks()
    {
        Assert.AreEqual(0, (await Chunk(Doc("", sections: [Section(0, 0, 0)]))).Count);
        Assert.AreEqual(0, (await Chunk(Doc("   \n\n  ", sections: [Section(0, 0, 7)]))).Count);
    }

    [TestMethod]
    public void TheRouteNamesItself_BecauseStep4StampsThatName()
    {
        // route_name on a chunk is the strategy's own Name, so a chunk cannot disagree with the
        // class that actually cut it.
        Assert.AreEqual("DeclaredBoundary", new DeclaredBoundaryStrategy().Name);
    }
}
