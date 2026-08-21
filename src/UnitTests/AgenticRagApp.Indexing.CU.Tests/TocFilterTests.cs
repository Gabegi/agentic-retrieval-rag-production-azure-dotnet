using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Services;

namespace RagApp.UnitTests.Indexing;

// The rule is deliberately conservative in ONE direction, and most of these tests pin that
// direction rather than the drop: a TOC left in the index is a bad row, a real section dropped
// is content nothing downstream can tell was ever there.
[TestClass]
public class TocFilterTests
{
    private static ChunkObject Chunk(string content, string? heading = null, string? path = null) =>
        new() { Content = content, HeadingText = heading, HeadingPath = path };

    private const string LeaderBody =
        "Inleiding ....... 3\nDefinities ....... 5\nAlgemene bepalingen ....... 9\nBijlage 1 ....... 21";

    [TestMethod]
    public void ATocTitleOverLeaderLines_IsDropped()
    {
        Assert.IsTrue(TocFilter.IsTableOfContents(Chunk(LeaderBody, "Inhoudsopgave")));
        Assert.IsTrue(TocFilter.IsTableOfContents(Chunk(LeaderBody, "Inhoud")));
    }

    [TestMethod]
    public void TheDoubledInhoudInhoudForm_IsDropped()
    {
        // "Inhoud Inhoud" is the observed rendering in the 260818 corpus.
        Assert.IsTrue(TocFilter.IsTableOfContents(Chunk(LeaderBody, "Inhoud Inhoud")));
    }

    [TestMethod]
    public void EntriesSeparatedByWhitespaceRatherThanDots_AreStillEntries()
    {
        // DI keeps the leader dots on some documents and collapses them to spaces on others.
        const string spaced = "Inleiding     3\nDefinities     5\nAlgemene bepalingen     9";

        Assert.IsTrue(TocFilter.IsTableOfContents(Chunk(spaced, "Inhoudsopgave")));
    }

    [TestMethod]
    public void ARealSectionWhoseNameStartsWithInhoud_IsKept()
    {
        // The whole reason the title test is a whole-title match and not a Contains: "Inhoud
        // van de zorgmap" is a real section about what a care folder holds.
        Assert.IsFalse(TocFilter.IsTableOfContents(Chunk(LeaderBody, "Inhoud van de zorgmap")));
    }

    [TestMethod]
    public void ATocTitleOverRealProse_IsKept()
    {
        const string prose =
            "Dit hoofdstuk beschrijft de inhoud van het dossier.\n" +
            "De medewerker legt vast welke zorg is geleverd.\n" +
            "Afwijkingen worden gemeld bij de teamleider.";

        Assert.IsFalse(TocFilter.IsTableOfContents(Chunk(prose, "Inhoud")));
    }

    [TestMethod]
    public void LeaderShapedLinesUnderANonTocHeading_AreKept()
    {
        // A rate appendix is also mostly short lines ending in numbers. Body shape alone must
        // never be enough.
        Assert.IsFalse(TocFilter.IsTableOfContents(Chunk(LeaderBody, "Artikel 4:15 Salarisschalen")));
    }

    [TestMethod]
    public void AChunkWithNoHeadingOfItsOwn_FallsBackToTheLeafOfItsPath()
    {
        Assert.IsTrue(TocFilter.IsTableOfContents(
            Chunk(LeaderBody, heading: null, path: "CAO VVT > Inhoudsopgave")));

        // The leaf ONLY: a real section nested under a TOC entry is not a TOC.
        Assert.IsFalse(TocFilter.IsTableOfContents(
            Chunk(LeaderBody, heading: null, path: "Inhoudsopgave > Artikel 3:5")));
    }

    [TestMethod]
    public void ABodyTooShortToHaveAShape_IsKept()
    {
        Assert.IsFalse(TocFilter.IsTableOfContents(Chunk("Inleiding ..... 3", "Inhoudsopgave")));
    }
}
