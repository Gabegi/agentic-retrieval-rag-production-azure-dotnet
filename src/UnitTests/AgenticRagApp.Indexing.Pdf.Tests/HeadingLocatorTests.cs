using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Indexing.Pdf.Utils;

namespace RagApp.UnitTests.Indexing;

[TestClass]
public class HeadingLocatorTests
{
    private static Heading H(string content, int offset, int page = 1, int depth = 1) =>
        new(content, "sectionHeading", offset, page, depth);

    private static IReadOnlyList<PageSpan> OnePage(string content, int page = 1) =>
        [new PageSpan(page, 0, content.Length, null, false)];

    [TestMethod]
    public void LocatesHeadingsByText_NotByRawOffset()
    {
        // The offsets here are deliberately wrong for the cleaned text - which is the real
        // situation, since Heading.Offset addresses DI's raw content and cleaning shifts
        // everything. A locator that trusted the offset would cut in the wrong place.
        var content = "Intro paragraph.\n\nEerste kop\n\nBody one.\n\nTweede kop\n\nBody two.";
        var result  = HeadingLocator.Locate(content, [H("Eerste kop", 9_999), H("Tweede kop", 12_345)], OnePage(content));

        Assert.AreEqual(2, result.HeadingsLocated);
        Assert.AreEqual(0, result.FailureRate);

        var sections = result.Sections;
        Assert.AreEqual("Eerste kop", sections.Single(s => s.HeadingText == "Eerste kop").HeadingText);
        Assert.IsTrue(content.Substring(sections[1].Start, sections[1].Length).Contains("Body one."));
    }

    [TestMethod]
    public void ContentBeforeTheFirstHeading_BecomesItsOwnSection()
    {
        // The preamble rule. Merging frontmatter into the first real section would attribute
        // a cover page or table of contents to a heading it has nothing to do with, and that
        // misattribution rides into the embedded text as a prefix.
        var content = "Cover page text.\n\nHoofdstuk 1\n\nBody.";
        var result  = HeadingLocator.Locate(content, [H("Hoofdstuk 1", 0)], OnePage(content));

        Assert.AreEqual(2, result.Sections.Count);
        Assert.IsNull(result.Sections[0].HeadingText);
        Assert.AreEqual(ChunkHeadingSource.None, result.Sections[0].HeadingSource);
        Assert.AreEqual(0, result.Sections[0].Start);
    }

    [TestMethod]
    public void NoPreambleSection_WhenTheDocumentOpensWithAHeading()
    {
        var content = "Hoofdstuk 1\n\nBody.";
        var result  = HeadingLocator.Locate(content, [H("Hoofdstuk 1", 0)], OnePage(content));

        Assert.AreEqual(1, result.Sections.Count);
        Assert.AreEqual("Hoofdstuk 1", result.Sections[0].HeadingText);
    }

    [TestMethod]
    public void PairedZeroBodyHeadings_AreMergedIntoOneSection()
    {
        // Hygienecode emits pairs where the first heading has no body before the second.
        // Left alone each pair produces a parent whose only content is its own heading line.
        var content = "3.3 Wat moet je doen\n\nActies als het misgaat\n\nDe echte inhoud staat hier.";
        var result  = HeadingLocator.Locate(
            content,
            [H("3.3 Wat moet je doen", 0), H("Acties als het misgaat", 30)],
            OnePage(content));

        Assert.AreEqual(1, result.PairedHeadingsMerged);
        Assert.AreEqual(1, result.Sections.Count);
        Assert.AreEqual("3.3 Wat moet je doen Acties als het misgaat", result.Sections[0].HeadingText);
        Assert.IsTrue(content.Substring(result.Sections[0].Start, result.Sections[0].Length)
                             .Contains("De echte inhoud"));
    }

    [TestMethod]
    public void NoHeadingsAnywhere_ProducesOneSectionCoveringTheDocument()
    {
        // Branch 5 of the cascade falls out of this rather than needing a route of its own.
        var content = "Just prose, no headings at all.";
        var result  = HeadingLocator.Locate(content, [], OnePage(content));

        Assert.AreEqual(1, result.Sections.Count);
        Assert.AreEqual(0, result.Sections[0].Start);
        Assert.AreEqual(content.Length, result.Sections[0].End);
        Assert.IsNull(result.Sections[0].HeadingText);
    }

    [TestMethod]
    public void RepeatedHeadingText_MatchesInDocumentOrder_NotTheFirstOccurrence()
    {
        // A running title or a term reused as a heading appears more than once. Matching the
        // first occurrence every time would collapse later sections onto the earlier one.
        var content = "Bijlage\n\nOne.\n\nMidden\n\nTwo.\n\nBijlage\n\nThree.";
        var result  = HeadingLocator.Locate(
            content,
            [H("Bijlage", 0), H("Midden", 10), H("Bijlage", 20)],
            OnePage(content));

        Assert.AreEqual(3, result.HeadingsLocated);
        CollectionAssert.AreEqual(
            new[] { 0, content.IndexOf("Midden", StringComparison.Ordinal), content.LastIndexOf("Bijlage", StringComparison.Ordinal) },
            result.Sections.Select(s => s.Start).ToArray());
    }

    [TestMethod]
    public void UnlocatableHeading_IsCountedAsAFailure_NotSilentlyDropped()
    {
        // The failure rate is the permanent form of the measurement that chose string
        // matching over rewriting PdfCleaner. If it starts moving, that decision is due to
        // be reopened - so an unfindable heading has to be visible, not absorbed.
        var content = "Only this text exists.";
        var result  = HeadingLocator.Locate(content, [H("Ontbrekende kop", 0)], OnePage(content));

        Assert.AreEqual(1, result.HeadingsTotal);
        Assert.AreEqual(0, result.HeadingsLocated);
        Assert.AreEqual(1.0, result.FailureRate);
    }

    [TestMethod]
    public void MergedTwoLineHeading_MatchesOnItsFirstLineOnly()
    {
        // A paired "Artikel 9" + title merge carries both lines in Content but its Offset
        // covers only the first paragraph, so only the first line is reliably contiguous.
        var content = "Artikel 9\n\nBegrippen\n\nBody text here.";
        var result  = HeadingLocator.Locate(content, [H("Artikel 9\nBegrippen", 0)], OnePage(content));

        Assert.AreEqual(1, result.HeadingsLocated);
        Assert.AreEqual(0, result.Sections[0].Start);
    }

    [TestMethod]
    public void EmptyContent_ProducesNoSections()
    {
        var result = HeadingLocator.Locate("", [H("Kop", 0)], []);

        Assert.AreEqual(0, result.Sections.Count);
        Assert.AreEqual(0, result.HeadingsLocated);
    }
}
