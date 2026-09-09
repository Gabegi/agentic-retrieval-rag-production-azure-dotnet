using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Utils;

namespace RagApp.UnitTests.Indexing;

// Span-direct boundaries (2026-09-09): a heading's section starts at Heading.Offset, which is
// CU's own utf16 span into the verbatim markdown and (per the cu-raw-response capture) starts at
// the "#" marker. Offsets in these tests are therefore computed with IndexOf against the content
// being cut, never hand-counted and never deliberately wrong - the string-search era, where a
// wrong offset was the premise, is what this file replaced.
[TestClass]
public class HeadingLocatorTests
{
    private static Heading H(string content, int offset, int page = 1, int depth = 1) =>
        new(content, "sectionHeading", offset, page, depth);

    // A heading at its own position in the content: the marker (if any) is where the span starts.
    private static Heading At(string content, string heading, string? markedAs = null) =>
        H(heading, content.IndexOf(markedAs ?? heading, StringComparison.Ordinal));

    private static IReadOnlyList<PageSpan> OnePage(string content, int page = 1) =>
        [new PageSpan(page, 0, content.Length, null)];

    [TestMethod]
    public void SectionsStartAtTheHeadingsOwnOffset()
    {
        var content = "Intro paragraph.\n\nEerste kop\n\nBody one.\n\nTweede kop\n\nBody two.";
        var result  = HeadingLocator.Locate(
            content, [At(content, "Eerste kop"), At(content, "Tweede kop")], OnePage(content));

        Assert.AreEqual(2, result.HeadingsLocated);
        Assert.AreEqual(0, result.FailureRate);

        var second = result.Sections.Single(s => s.HeadingText == "Tweede kop");
        Assert.AreEqual(content.IndexOf("Tweede kop", StringComparison.Ordinal), second.Start);
        Assert.IsTrue(content[result.Sections[1].Start..result.Sections[1].End].Contains("Body one."));
    }

    [TestMethod]
    public void ASpanThatStartsAtTheMarker_KeepsTheMarkerWithItsHeading()
    {
        // The shape the raw capture showed: paragraph content "Tweede kop", span covering
        // "## Tweede kop". Cutting at the span leaves nothing dangling in the previous section.
        var content = "Intro paragraaf.\n\n## Tweede kop\n\nBody twee.";
        var result  = HeadingLocator.Locate(
            content, [At(content, "Tweede kop", markedAs: "## Tweede kop")], OnePage(content));

        var preamble = result.Sections[0];
        var section  = result.Sections[1];

        Assert.IsFalse(content[preamble.Start..preamble.End].TrimEnd().EndsWith('#'));
        Assert.IsTrue(content[section.Start..section.End].StartsWith("## Tweede kop"));
    }

    [TestMethod]
    public void ContentBeforeTheFirstHeading_BecomesItsOwnSection()
    {
        // The preamble rule. Merging frontmatter into the first real section would attribute
        // a cover page or table of contents to a heading it has nothing to do with, and that
        // misattribution rides into the embedded text as a prefix.
        var content = "Cover page text.\n\nHoofdstuk 1\n\nBody.";
        var result  = HeadingLocator.Locate(content, [At(content, "Hoofdstuk 1")], OnePage(content));

        Assert.AreEqual(2, result.Sections.Count);
        Assert.IsNull(result.Sections[0].HeadingText);
        Assert.AreEqual(ChunkHeadingSource.None, result.Sections[0].HeadingSource);
        Assert.AreEqual(0, result.Sections[0].Start);
    }

    [TestMethod]
    public void NoPreambleSection_WhenTheDocumentOpensWithAHeading()
    {
        var content = "Hoofdstuk 1\n\nBody.";
        var result  = HeadingLocator.Locate(content, [At(content, "Hoofdstuk 1")], OnePage(content));

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
            [At(content, "3.3 Wat moet je doen"), At(content, "Acties als het misgaat")],
            OnePage(content));

        Assert.AreEqual(1, result.PairedHeadingsMerged);
        Assert.AreEqual(1, result.Sections.Count);
        Assert.AreEqual("3.3 Wat moet je doen Acties als het misgaat", result.Sections[0].HeadingText);
        Assert.IsTrue(content[result.Sections[0].Start..result.Sections[0].End].Contains("De echte inhoud"));
    }

    [TestMethod]
    public void PairedZeroBodyHeadings_StillMerge_WhenTheSpanIncludesTheMarker()
    {
        // The body slice starts at the marker, so the zero-body comparison must strip it -
        // otherwise every marker'd pair reads as having "### " of body and never merges.
        var content = "### 3.3 Wat moet je doen\n\n### Acties als het misgaat\n\nDe echte inhoud staat hier.";
        var result  = HeadingLocator.Locate(
            content,
            [At(content, "3.3 Wat moet je doen", "### 3.3"), At(content, "Acties als het misgaat", "### Acties")],
            OnePage(content));

        Assert.AreEqual(1, result.PairedHeadingsMerged);
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
    public void RepeatedHeadingText_IsToldApartByOffset()
    {
        // A running title or a term reused as a heading appears more than once. The offsets
        // are different positions, so no text search and no cursor is needed to keep them apart.
        var content = "Bijlage\n\nOne.\n\nMidden\n\nTwo.\n\nBijlage\n\nThree.";
        var result  = HeadingLocator.Locate(
            content,
            [H("Bijlage", 0), At(content, "Midden"), H("Bijlage", content.LastIndexOf("Bijlage", StringComparison.Ordinal))],
            OnePage(content));

        Assert.AreEqual(3, result.HeadingsLocated);
        CollectionAssert.AreEqual(
            new[] { 0, content.IndexOf("Midden", StringComparison.Ordinal), content.LastIndexOf("Bijlage", StringComparison.Ordinal) },
            result.Sections.Select(s => s.Start).ToArray());
    }

    [TestMethod]
    public void AnOffsetBeyondTheContent_IsCountedAsAFailure_NotClamped()
    {
        // The service's span addresses a string its markdown is not. Clamping to the end would
        // open an empty section there and hide the disagreement; the failure rate is where it
        // has to show.
        var content = "Only this text exists.";
        var result  = HeadingLocator.Locate(content, [H("Ontbrekende kop", 9_999)], OnePage(content));

        Assert.AreEqual(1, result.HeadingsTotal);
        Assert.AreEqual(0, result.HeadingsLocated);
        Assert.AreEqual(1.0, result.FailureRate);
        Assert.AreEqual(1, result.Sections.Count, "the document is still one section");
        Assert.IsNull(result.Sections[0].HeadingText);
    }

    [TestMethod]
    public void MergedTwoLineHeading_IsStoredSpaceJoined()
    {
        var content = "Artikel 9\n\nBegrippen\n\nBody text here.";
        var result  = HeadingLocator.Locate(content, [H("Artikel 9\nBegrippen", 0)], OnePage(content));

        Assert.AreEqual(1, result.HeadingsLocated);
        Assert.AreEqual(0, result.Sections[0].Start);
        Assert.AreEqual("Artikel 9 Begrippen", result.Sections[0].HeadingText);
        Assert.IsFalse(result.Sections[0].HeadingText!.Contains('\n'));
    }

    // The gate that stops the merge from folding what is really two sections: a bare numbered
    // label followed by another heading is two short articles.
    [TestMethod]
    public void BareNumberedLabelWithNoBody_IsNotMergedWithTheNextHeading()
    {
        var content = "Artikel 8\n\nArtikel 9\n\nDe echte inhoud staat hier.";
        var result  = HeadingLocator.Locate(
            content,
            [At(content, "Artikel 8"), At(content, "Artikel 9")],
            OnePage(content));

        Assert.AreEqual(0, result.PairedHeadingsMerged);
        Assert.AreEqual(2, result.Sections.Count);
        Assert.AreEqual("Artikel 8", result.Sections[0].HeadingText);
        Assert.AreEqual("Artikel 9", result.Sections[1].HeadingText);
    }

    [TestMethod]
    public void EmptyContent_ProducesNoSections()
    {
        var result = HeadingLocator.Locate("", [H("Kop", 0)], []);

        Assert.AreEqual(0, result.Sections.Count);
        Assert.AreEqual(0, result.HeadingsLocated);
        Assert.AreEqual(1, result.HeadingsTotal, "the total is what arrived, even when nothing could be located");
        Assert.AreEqual(0, result.HeadingsWithoutOffset);
    }

    // ── the null offset ──────────────────────────────────────────────────────
    //
    // A null offset means the paragraph carried no span at all - explicitly not 0, since 0 is a
    // real offset and cannot double as "unknown". Measured at 0 of 1,273 headings across the big
    // four, so it is counted as an anomaly rather than absorbed: the previous locator gave such a
    // heading its predecessor's offset and searched from there, which is a guess about position.

    private static Heading NoOffset(string content, int page = 1) =>
        new(content, "sectionHeading", null, page, 1);

    [TestMethod]
    public void AHeadingWithNoOffset_OpensNoSection_AndIsCounted()
    {
        var content = "Eerste kop\n\nBody een.\n\nTweede kop\n\nBody twee.\n\nDerde kop\n\nBody drie.";

        var result = HeadingLocator.Locate(
            content,
            [At(content, "Eerste kop"), NoOffset("Tweede kop"), At(content, "Derde kop")],
            OnePage(content));

        Assert.AreEqual(3, result.HeadingsTotal);
        Assert.AreEqual(2, result.HeadingsLocated);
        Assert.AreEqual(1, result.HeadingsWithoutOffset);
        CollectionAssert.AreEqual(
            new[] { "Eerste kop", "Derde kop" },
            result.Sections.Select(s => s.HeadingText).ToArray(),
            "the offsetless heading has no position and therefore no section; its text stays in Eerste kop's body");
        Assert.IsTrue(content[result.Sections[0].Start..result.Sections[0].End].Contains("Tweede kop"));
    }

    [TestMethod]
    public void EveryHeadingWithoutAnOffset_LeavesTheDocumentAsOneSection()
    {
        var content = "Kop A\n\nBody een.\n\nKop B\n\nBody twee.";

        var result = HeadingLocator.Locate(
            content, [NoOffset("Kop A"), NoOffset("Kop B")], OnePage(content));

        Assert.AreEqual(2, result.HeadingsWithoutOffset);
        Assert.AreEqual(0, result.HeadingsLocated);
        Assert.AreEqual(1, result.Sections.Count);
        Assert.IsNull(result.Sections[0].HeadingText);
    }

    [TestMethod]
    public void HeadingsArrivingOutOfOrder_AreSortedByOffset()
    {
        // CuOutlineHelper's forward walk already delivers reading order; the sort stays so the
        // strategy does not depend on an upstream guarantee nothing states.
        var content = "Eerste kop\n\nBody een.\n\nTweede kop\n\nBody twee.";

        var result = HeadingLocator.Locate(
            content, [At(content, "Tweede kop"), At(content, "Eerste kop")], OnePage(content));

        CollectionAssert.AreEqual(
            new[] { "Eerste kop", "Tweede kop" },
            result.Sections.Select(s => s.HeadingText).ToArray());
    }

    [TestMethod]
    public void ThePreamblesPage_IsZeroWhenNoSpanContainsOffsetZero()
    {
        // The same "unknown = 0" answer CuPageHelper.PageAt and PageResolver give: no guess at
        // the first span's page.
        var content = "Cover.\n\nKop\n\nBody.";
        var spans   = new PageSpan[] { new(3, content.IndexOf("Kop", StringComparison.Ordinal), 4, null) };

        var result = HeadingLocator.Locate(content, [At(content, "Kop")], spans);

        Assert.AreEqual(0, result.Sections[0].PageNumber);
    }

    // The 260819 breadcrumb residue. "Artikel 1:6" is vacant, so it has no body, and the
    // paired-zero-body merge folded its title into the next article's - producing one segment
    // naming both, "Artikel 1:6 (vacant) Artikel 1:7 Toepassing CAO op relatiepartner". That
    // reads as two Artikel levels to anything measuring the path (408 such paths in the run)
    // and carries a vacant article's number into a real article's identity, unrecoverably,
    // since the two titles are now one string.
    [TestMethod]
    public void TwoArticlesAtTheSameLevel_AreNotMerged_EvenWhenTheFirstHasNoBody()
    {
        var content = "Artikel 1:6 (vacant)\n\nArtikel 1:7 Toepassing CAO op relatiepartner\n\nDe echte inhoud staat hier.";
        var result  = HeadingLocator.Locate(
            content,
            [At(content, "Artikel 1:6 (vacant)"), At(content, "Artikel 1:7 Toepassing CAO op relatiepartner")],
            OnePage(content));

        Assert.AreEqual(0, result.PairedHeadingsMerged);
        Assert.AreEqual(2, result.Sections.Count);
        Assert.AreEqual("Artikel 1:6 (vacant)", result.Sections[0].HeadingText);
        Assert.AreEqual("Artikel 1:7 Toepassing CAO op relatiepartner", result.Sections[1].HeadingText);
    }

    // Same rule, the shape the bare-label gate could never catch: both headings carry titles,
    // so BareNumberedLabelWithWord does not match either, but they are still siblings.
    [TestMethod]
    public void TwoTitledArticlesAtTheSameLevel_AreNotMerged()
    {
        var content = "Artikel 8 Begrippen\n\nArtikel 9 Reikwijdte\n\nDe echte inhoud staat hier.";
        var result  = HeadingLocator.Locate(
            content,
            [At(content, "Artikel 8 Begrippen"), At(content, "Artikel 9 Reikwijdte")],
            OnePage(content));

        Assert.AreEqual(0, result.PairedHeadingsMerged);
        Assert.AreEqual("Artikel 8 Begrippen",  result.Sections[0].HeadingText);
        Assert.AreEqual("Artikel 9 Reikwijdte", result.Sections[1].HeadingText);
    }

    // Equal-depth dotted numbers are siblings by the same rule - "3.3" and "3.4", not "3" and
    // "3.3". This is the case the level key's segment count exists to tell apart.
    [TestMethod]
    public void TwoDottedHeadingsAtEqualDepth_AreNotMerged()
    {
        var content = "3.3 Wat moet je doen\n\n3.4 Wie is verantwoordelijk\n\nDe echte inhoud staat hier.";
        var result  = HeadingLocator.Locate(
            content,
            [At(content, "3.3 Wat moet je doen"), At(content, "3.4 Wie is verantwoordelijk")],
            OnePage(content));

        Assert.AreEqual(0, result.PairedHeadingsMerged);
        Assert.AreEqual(2, result.Sections.Count);
    }

    // The gate must not swallow the case the merge exists for. A heading and its continuation
    // are at DIFFERENT levels (or the continuation has no shape at all), so the pair still
    // merges - PairedZeroBodyHeadings_AreMergedIntoOneSection's premise, restated here against
    // a shaped first heading to prove the check is what decides it.
    [TestMethod]
    public void AShapedHeadingFollowedByAnUnshapedContinuation_StillMerges()
    {
        var content = "3.3 Wat moet je doen\n\nActies als het misgaat\n\nDe echte inhoud staat hier.";
        var result  = HeadingLocator.Locate(
            content,
            [At(content, "3.3 Wat moet je doen"), At(content, "Acties als het misgaat")],
            OnePage(content));

        Assert.AreEqual(1, result.PairedHeadingsMerged);
        Assert.AreEqual("3.3 Wat moet je doen Acties als het misgaat", result.Sections[0].HeadingText);
    }

    // A Hoofdstuk parent above an Artikel leaf is a real hierarchy, not a sibling pair - the
    // level keys differ, so an empty chapter heading still folds into the article beneath it.
    [TestMethod]
    public void AChapterAboveAnArticle_IsNotTreatedAsASibling()
    {
        var content = "Hoofdstuk 1 De arbeidsovereenkomst\n\nArtikel 1 de arbeidsovereenkomst\n\nDe echte inhoud staat hier.";
        var result  = HeadingLocator.Locate(
            content,
            [At(content, "Hoofdstuk 1 De arbeidsovereenkomst"), At(content, "Artikel 1 de arbeidsovereenkomst")],
            OnePage(content));

        Assert.AreEqual(1, result.PairedHeadingsMerged);
    }
}
