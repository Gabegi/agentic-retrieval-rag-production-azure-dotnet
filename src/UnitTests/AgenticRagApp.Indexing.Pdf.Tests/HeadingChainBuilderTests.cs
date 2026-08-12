using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Indexing.Pdf.Utils;

namespace RagApp.UnitTests.Indexing;

[TestClass]
public class HeadingChainBuilderTests
{
    private static Heading H(string content, int offset) =>
        new(content, "sectionHeading", offset, PageNumber: 1);

    private static SectionInfo S(int offset, int length) =>
        new([new SectionSpan(offset, length)], [], []);

    [TestMethod]
    public void NestedSections_ProduceAnAncestorChain()
    {
        // DI's sections nest: a chapter's span contains its subsections'. Phase A measured
        // section starts and heading offsets as identical, so the containment tree can be read
        // as a heading hierarchy without touching Heading.Depth.
        var headings = new[] { H("Hoofdstuk 3", 0), H("3.2 Dosering", 100), H("3.2.1 Details", 150) };
        var sections = new[] { S(0, 500), S(100, 200), S(150, 50) };

        var chains = HeadingChainBuilder.Build(sections, headings);

        Assert.AreEqual("Hoofdstuk 3 > 3.2 Dosering > 3.2.1 Details",
            HeadingChainBuilder.Path(chains, 150, "3.2.1 Details"));
    }

    [TestMethod]
    public void TopLevelHeading_HasNoAncestors()
    {
        var headings = new[] { H("Hoofdstuk 1", 0) };
        var sections = new[] { S(0, 500) };

        var chains = HeadingChainBuilder.Build(sections, headings);

        Assert.AreEqual("Hoofdstuk 1", HeadingChainBuilder.Path(chains, 0, "Hoofdstuk 1"));
    }

    [TestMethod]
    public void OutermostAncestorComesFirst()
    {
        // Ordered by span width, not by document position: the widest enclosing section is the
        // top of the chain regardless of the order the sections happen to be listed in.
        var headings = new[] { H("Deel A", 0), H("Kop", 50) };
        var sections = new[] { S(50, 10), S(0, 900) };

        var chains = HeadingChainBuilder.Build(sections, headings);

        Assert.AreEqual("Deel A > Kop", HeadingChainBuilder.Path(chains, 50, "Kop"));
    }

    [TestMethod]
    public void SectionWithNoMatchingHeading_ContributesNoTitle()
    {
        // The document-spanning root section typically opens at offset 0 with no heading of
        // its own. It must not put an empty segment into the chain.
        var headings = new[] { H("Kop", 100) };
        var sections = new[] { S(0, 900), S(100, 50) };

        var chains = HeadingChainBuilder.Build(sections, headings);

        Assert.AreEqual("Kop", HeadingChainBuilder.Path(chains, 100, "Kop"));
    }

    [TestMethod]
    public void RepeatedTitle_IsNotEmittedTwice()
    {
        // A section whose span starts at its own heading can appear both as an ancestor and as
        // the leaf. "Hoofdstuk 3 > Hoofdstuk 3" reads as a structure error in a citation.
        var headings = new[] { H("Hoofdstuk 3", 0), H("Kop", 10) };
        var sections = new[] { S(0, 500), S(0, 400) };

        var chains = HeadingChainBuilder.Build(sections, headings);

        Assert.AreEqual("Hoofdstuk 3 > Kop", HeadingChainBuilder.Path(chains, 10, "Kop"));
    }

    [TestMethod]
    public void MergedHeading_UsesItsFirstLineForAncestorMatching()
    {
        var headings = new[] { H("Hoofdstuk 1", 0), H("Artikel 9\nBegrippen", 40) };
        var sections = new[] { S(0, 500), S(40, 60) };

        var chains = HeadingChainBuilder.Build(sections, headings);

        Assert.AreEqual("Hoofdstuk 1 > Artikel 9 Begrippen",
            HeadingChainBuilder.Path(chains, 40, "Artikel 9 Begrippen"));
    }

    [TestMethod]
    public void NoSections_FallsBackToTheHeadingAlone()
    {
        // Most of this corpus has no usable outline, and a document DI gave no section tree
        // for still needs a heading path - just an unnested one.
        var chains = HeadingChainBuilder.Build([], [H("Kop", 0)]);

        Assert.AreEqual("Kop", HeadingChainBuilder.Path(chains, 0, "Kop"));
    }

    [TestMethod]
    public void NoHeadingText_HasNoPath()
    {
        var chains = HeadingChainBuilder.Build([S(0, 100)], [H("Kop", 0)]);

        Assert.IsNull(HeadingChainBuilder.Path(chains, 0, null));
        Assert.IsNull(HeadingChainBuilder.Path(chains, 0, "   "));
    }
}
