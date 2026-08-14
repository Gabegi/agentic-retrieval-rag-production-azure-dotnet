using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Indexing.Pdf.Services;
using AgenticRagApp.Indexing.Pdf.Utils;

namespace RagApp.UnitTests.Indexing;

[TestClass]
public class SectionCascadeStrategyTests
{
    private static SectionCascadeStrategy Strategy(int tokenCeiling = SectionSplitter.DefaultTokenCeiling) =>
        new(new SectionSplitter(), tokenCeiling);

    private static Heading H(string content, int offset, int page = 1, int depth = 1) =>
        new(content, "sectionHeading", offset, page, depth);

    private static PdfExtractionDocument Doc(
        string content,
        IReadOnlyList<Heading>? headings = null,
        IReadOnlyList<SectionInfo>? sections = null) =>
        new(
            SourceId: "doc.pdf", Content: content,
            PageSpans: [new PageSpan(1, 0, content.Length, null, false)],
            Title: "T", Author: null, CreatedAt: null, ModDate: null, PageCount: null,
            LastModifiedDate: null, ZenyaDocumentId: null, ZenyaVersion: null,
            ZenyaStatus: null, ZenyaUrl: null, Bookmarks: [],
            PageBreadcrumbs: new Dictionary<int, string>(),
            Sections: sections ?? [], Headings: headings ?? [], Boilerplate: [],
            Tables: [], SelectionMarks: [], Figures: [], Lines: [],
            Profile: null, Language: null);

    private static string Prose(int chars) =>
        string.Join(" ", Enumerable.Repeat("woord", chars / 6)) + ".";

    [TestMethod]
    public void EmptyDocument_ProducesNothing()
    {
        Assert.AreEqual(0, Strategy().Chunk(Doc("")).Units.Count);
        Assert.AreEqual(0, Strategy().Chunk(Doc("   \n\n ")).Units.Count);
    }

    [TestMethod]
    public void EachSectionBecomesItsOwnUnit_WhenUnderTheCeiling()
    {
        var content = "Kop een\n\nBody one.\n\nKop twee\n\nBody two.";
        var outcome = Strategy().Chunk(Doc(content, [H("Kop een", 0), H("Kop twee", 20)]));

        Assert.AreEqual(2, outcome.Units.Count);
        CollectionAssert.AreEqual(new[] { 0, 1 }, outcome.Units.Select(u => u.SectionIndex).ToArray());
        Assert.IsTrue(outcome.Units.All(u => u.ChildIndex == 0));
    }

    [TestMethod]
    public void OversizedSection_SplitsIntoNumberedChildrenOfOneSection()
    {
        var body    = Prose(3_000);
        var outcome = Strategy(tokenCeiling: 60).Chunk(Doc(body));

        Assert.IsTrue(outcome.Units.Count > 1);
        Assert.IsTrue(outcome.Units.All(u => u.SectionIndex == 0));
        CollectionAssert.AreEqual(
            Enumerable.Range(0, outcome.Units.Count).ToArray(),
            outcome.Units.Select(u => u.ChildIndex).ToArray());
    }

    [TestMethod]
    public void ParentText_IsOnlyStoredWhenTheSectionWasActuallySplit()
    {
        // Phase A measured 83-87% of sections as never split, so storing the section text on
        // every child unconditionally would roughly double the corpus's stored text while
        // saying nothing - on a single-child section the child IS the section.
        var single = Strategy().Chunk(Doc("Short body."));
        var split  = Strategy(tokenCeiling: 60).Chunk(Doc(Prose(3_000)));

        Assert.IsNull(single.Units[0].ParentText);
        Assert.IsTrue(split.Units.All(u => u.ParentText is not null));
    }

    [TestMethod]
    public void StartOffsets_AdvanceEvenWhenOverlapMakesChildrenShareText()
    {
        // Page attribution reads Start against the page map, so it has to track forward. An
        // IndexOf from the section start would keep resolving to the earlier copy once two
        // consecutive children share overlapped text, and every page after the first overlap
        // would be attributed wrongly.
        var outcome = Strategy(tokenCeiling: 60).Chunk(Doc(Prose(3_000)));

        var starts = outcome.Units.Select(u => u.Start).ToList();
        CollectionAssert.AreEqual(starts.OrderBy(s => s).ToList(), starts);
        Assert.AreEqual(starts.Count, starts.Distinct().Count());
    }

    [TestMethod]
    public void HeadingChainIsCarriedOntoEveryChildOfASection()
    {
        var content  = "Hoofdstuk 1\n\n" + Prose(3_000);
        var sections = new[] { new SectionInfo([new SectionSpan(0, 5_000)], [], []) };
        var outcome  = Strategy(tokenCeiling: 60).Chunk(Doc(content, [H("Hoofdstuk 1", 0)], sections));

        Assert.IsTrue(outcome.Units.Count > 1);
        Assert.IsTrue(outcome.Units.All(u => u.HeadingText == "Hoofdstuk 1"));
        Assert.IsTrue(outcome.Units.All(u => u.HeadingPath == "Hoofdstuk 1"));
    }

    [TestMethod]
    public void DiagnosticsReportHeadingLocation()
    {
        // The standing evidence for locating headings by string match rather than rewriting
        // PdfCleaner - it has to be reported every run, not measured once.
        var content = "Kop een\n\nBody.";
        var outcome = Strategy().Chunk(Doc(content, [H("Kop een", 0), H("Ontbreekt", 99)]));

        Assert.AreEqual(2, outcome.HeadingsTotal);
        Assert.AreEqual(1, outcome.HeadingsLocated);
    }

    [TestMethod]
    public void NoHeadings_StillProducesUnits_AsOneDegenerateSection()
    {
        var outcome = Strategy().Chunk(Doc("Just prose, no headings anywhere."));

        Assert.AreEqual(1, outcome.Units.Count);
        Assert.AreEqual(0, outcome.Units[0].SectionIndex);
        Assert.IsNull(outcome.Units[0].HeadingText);
        Assert.AreEqual(ChunkHeadingSource.None, outcome.Units[0].HeadingSource);
    }

    [TestMethod]
    public void EveryUnitIsAChildGrain()
    {
        var outcome = Strategy().Chunk(Doc("Body."));

        Assert.IsTrue(outcome.Units.All(u => u.Grain == ChunkGrain.Child));
    }
}
