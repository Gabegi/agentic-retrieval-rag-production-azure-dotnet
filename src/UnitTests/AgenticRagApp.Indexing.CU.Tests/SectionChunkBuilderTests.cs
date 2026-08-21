using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Services;
using AgenticRagApp.Indexing.CU.Utils;

namespace RagApp.UnitTests.Indexing;

// Route 1's counterpart to FlatChunkBuilder, new with chunking-done.md §2. The two builders
// exist separately because the heading fields are not a variation on one shape - they are the
// difference between the routes, and a single builder taking nullable heading arguments would
// let a route 2 caller pass a heading it does not have.
[TestClass]
public class SectionChunkBuilderTests
{
    private static LocatedSection Section(
        int index          = 3,
        string? headingText = "Artikel 9 Begrippen",
        string? headingPath = "Hoofdstuk 1 > Artikel 9 Begrippen",
        string headingSource = ChunkHeadingSource.DiHeading,
        int depth          = 2,
        int start          = 100,
        int end            = 400,
        bool located       = true) =>
        new(Index:         index,
            HeadingText:   headingText,
            HeadingPath:   headingPath,
            HeadingSource: headingSource,
            Depth:         depth,
            Start:         start,
            End:           end,
            PageNumber:    4,
            Located:       located);

    private static ContentPiece Piece(string text, int start, BoundaryLevel level = BoundaryLevel.None) =>
        new(text, start, text.Length, level);

    [TestMethod]
    public void SectionIndexComesFromTheSection_AndChildIndexRestartsInsideIt()
    {
        // Chunk identity is (SectionIndex, ChildIndex), which is why the builder is called once
        // per section: a running document-wide counter would renumber every chunk below an
        // inserted section, and an id change is a delete-plus-insert in the index.
        var chunks = SectionChunkBuilder.Build(
            Section(index: 7), [Piece("een", 100), Piece("twee", 110), Piece("drie", 120)]);

        CollectionAssert.AreEqual(new[] { 7, 7, 7 }, chunks.Select(c => c.SectionIndex).ToArray());
        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, chunks.Select(c => c.ChildIndex).ToArray());
    }

    [TestMethod]
    public void ASectionThatFitWhole_IsJustNEqualsOne()
    {
        // The 83-87% path is not a special shape - it is one child.
        var chunks = SectionChunkBuilder.Build(Section(), [Piece("de hele sectie", 100)]);

        Assert.AreEqual(1, chunks.Count);
        Assert.AreEqual(0, chunks[0].ChildIndex);
        Assert.AreEqual(BoundaryLevel.None, chunks[0].BoundaryLevel);
    }

    [TestMethod]
    public void TheSectionsHeadingIsPassedThroughOntoEveryChild()
    {
        var chunks = SectionChunkBuilder.Build(Section(), [Piece("een", 100), Piece("twee", 200)]);

        foreach (var chunk in chunks)
        {
            Assert.AreEqual("Artikel 9 Begrippen", chunk.HeadingText);
            Assert.AreEqual("Hoofdstuk 1 > Artikel 9 Begrippen", chunk.HeadingPath);
            Assert.AreEqual(2, chunk.HeadingDepth);
            Assert.AreEqual(ChunkHeadingSource.DiHeading, chunk.HeadingSource);
            Assert.IsTrue(chunk.HeadingLocated);
        }
    }

    [TestMethod]
    public void APreambleSection_IsLocatedButNotHeadingLocated()
    {
        // The contradiction this class was written to prevent. A preamble is a REAL route 1
        // section - Located true, because its boundary was found - with HeadingSource "none"
        // and no heading at all. A flat `true` here would read as a successfully anchored
        // heading in any aggregate that counts one without reading the other, which is exactly
        // the bug FlatChunkBuilder's comment says never to reproduce.
        var preamble = Section(
            headingText: null, headingPath: null,
            headingSource: ChunkHeadingSource.None, depth: 0, located: true);

        var chunk = SectionChunkBuilder.Build(preamble, [Piece("omslagtekst", 0)])[0];

        Assert.IsFalse(chunk.HeadingLocated);
        Assert.AreEqual(ChunkHeadingSource.None, chunk.HeadingSource);
        Assert.IsNull(chunk.HeadingText);
    }

    [TestMethod]
    public void AnUnlocatedSectionWithARealHeading_IsAlsoNotHeadingLocated()
    {
        // Both halves come off the section rather than being asserted here, so either one being
        // false is enough.
        var chunk = SectionChunkBuilder.Build(Section(located: false), [Piece("tekst", 100)])[0];

        Assert.IsFalse(chunk.HeadingLocated);
        Assert.AreEqual(ChunkHeadingSource.DiHeading, chunk.HeadingSource, "the source still says where the heading came from");
    }

    [TestMethod]
    public void ContentIsTheBareBody_AndTheCoordinatesAreThePiecesOwn()
    {
        // Exactly as in FlatChunkBuilder: the prefix is priced before the cut but not carried,
        // so Content stays a window onto the source.
        var chunk = SectionChunkBuilder.Build(Section(), [Piece("De werkgever betaalt.", 100)])[0];

        Assert.AreEqual("De werkgever betaalt.", chunk.Content);
        Assert.AreEqual(100, chunk.Start);
        Assert.AreEqual(21, chunk.Length);
        Assert.IsFalse(chunk.Content.Contains("Artikel 9"), "the heading is not prepended into the body");
    }

    [TestMethod]
    public void PieceFlagsAreCarried_WhichIsWhatMakesTheOversizedPathVisible()
    {
        // Both are None/false for a section that fit whole - the honest value there - but they
        // stop being so the moment the oversized path starts cutting.
        var piece = new ContentPiece("| a | 1 |", 100, 9, BoundaryLevel.TableRow, Degraded: true, IsOverlap: true);

        var chunk = SectionChunkBuilder.Build(Section(), [piece])[0];

        Assert.AreEqual(BoundaryLevel.TableRow, chunk.BoundaryLevel);
        Assert.IsTrue(chunk.Degraded);
        Assert.IsTrue(chunk.IsOverlap);
    }

    [TestMethod]
    public void NoPieces_MeansNoChunks()
    {
        Assert.AreEqual(0, SectionChunkBuilder.Build(Section(), []).Count);
    }
}
