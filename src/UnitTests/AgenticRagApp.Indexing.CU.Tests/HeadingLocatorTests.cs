using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Utils;

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
    public void AMarkdownMarker_StaysWithItsHeading_NotWithThePreviousSection()
    {
        // DI renders headings as markdown ("## Kop") but the text match lands on the heading
        // TEXT, so the cut used to leave "## " dangling at the end of the previous section -
        // 1,754 of 2,997 chunks in the 260818 index ended in a bare marker line.
        var content = "Intro paragraaf.\n\n## Tweede kop\n\nBody twee.";
        var result  = HeadingLocator.Locate(content, [H("Tweede kop", 0)], OnePage(content));

        var preamble = result.Sections[0];
        var section  = result.Sections[1];

        Assert.IsFalse(content[preamble.Start..preamble.End].TrimEnd().EndsWith('#'),
            "previous section must not end in a dangling marker");
        Assert.IsTrue(content[section.Start..section.End].StartsWith("## Tweede kop"),
            "the marker belongs to the heading's own section");
    }

    [TestMethod]
    public void AHashInsideRunningText_IsNotAbsorbedAsAMarker()
    {
        // Only a marker that starts its own line is pulled in. Here the match lands on "Kop"
        // directly after a mid-sentence '#' - the walk-back must leave that '#' where it is,
        // in the preceding text, rather than treating it as the heading's marker.
        var content = "Zie ook #Kop voor details.\n\nBody.";
        var result  = HeadingLocator.Locate(content, [H("Kop", 0)], OnePage(content));

        var section = result.Sections.Single(s => s.HeadingText == "Kop");
        Assert.AreEqual('K', content[section.Start]);
        Assert.AreEqual('#', content[section.Start - 1], "the mid-text '#' must not be absorbed");
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

        // Matched on the first line, but STORED whole - every line, space-joined. It used to be
        // stored as heading.Content.Trim(), so the newline rode into heading_text, heading_path
        // and the embedded prefix, and rendered as a line break mid-citation.
        Assert.AreEqual("Artikel 9 Begrippen", result.Sections[0].HeadingText);
        Assert.IsFalse(result.Sections[0].HeadingText!.Contains('\n'));
    }

    // The gate that stops this rule from re-merging what extraction deliberately kept apart.
    // GetHeadingsHelper refuses to merge a run that starts with a bare numbered label - two
    // consecutive "Artikel" markers are separate short articles - and this rule had no such
    // check, so it merged them back on the cleaned text a step later.
    [TestMethod]
    public void BareNumberedLabelWithNoBody_IsNotMergedWithTheNextHeading()
    {
        var content = "Artikel 8\n\nArtikel 9\n\nDe echte inhoud staat hier.";
        var result  = HeadingLocator.Locate(
            content,
            [H("Artikel 8", 0), H("Artikel 9", 11)],
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

    // ── ordering, and the null offset (chunking-done.md §1) ───────────────────
    //
    // Offset is DI's RAW-content offset. It is used only to ORDER headings, never to slice:
    // cleaning changes length (a measured 1.066-1.202x drift) but is monotonic, so order
    // survives what position does not. A null offset means the paragraph carried no spans at
    // all - explicitly not 0, since 0 is a real offset and cannot double as "unknown".

    private static Heading NoOffset(string content, int page = 1) =>
        new(content, "sectionHeading", null, page, 1);

    [TestMethod]
    public void AHeadingWithNoOffset_InheritsTheLastOneSeen_AndStaysWithItsNeighbours()
    {
        // The change §1 made. The previous `?? int.MaxValue` sent an offsetless heading to the
        // END of the document - the one position it is guaranteed not to occupy - and took its
        // section boundary with it. Because the input IS in reading order, the one thing known
        // about such a heading is which headings it came after, so it inherits their offset.
        var content = "Eerste kop\n\nBody een.\n\nTweede kop\n\nBody twee.\n\nDerde kop\n\nBody drie.";

        var result = HeadingLocator.Locate(
            content,
            [H("Eerste kop", 100), NoOffset("Tweede kop"), H("Derde kop", 300)],
            OnePage(content));

        Assert.AreEqual(3, result.HeadingsLocated);
        CollectionAssert.AreEqual(
            new[] { "Eerste kop", "Tweede kop", "Derde kop" },
            result.Sections.Select(s => s.HeadingText).ToArray(),
            "the offsetless heading kept its place in reading order");
    }

    [TestMethod]
    public void AnOffsetlessHeadingIsCounted_SoAnExtractionAnomalyIsVisible()
    {
        // Zero on every document measured so far - 0 of 1,273 across the big four - which is
        // exactly why it is counted rather than assumed. A nonzero value means extraction
        // handed us a heading whose paragraph carried no spans, and the section boundary it
        // opens rests on a fallback.
        var content = "Eerste kop\n\nBody een.\n\nTweede kop\n\nBody twee.";

        var withNull = HeadingLocator.Locate(
            content, [H("Eerste kop", 0), NoOffset("Tweede kop")], OnePage(content));

        var withoutNull = HeadingLocator.Locate(
            content, [H("Eerste kop", 0), H("Tweede kop", 20)], OnePage(content));

        Assert.AreEqual(1, withNull.HeadingsWithoutOffset);
        Assert.AreEqual(0, withoutNull.HeadingsWithoutOffset, "the normal path, and the corpus's measured value");
    }

    [TestMethod]
    public void EveryHeadingWithoutAnOffset_KeepsArrivalOrder()
    {
        // A whole document's worth of anomalies: with nothing to inherit, the carried offset
        // stays 0 for all of them and the arrival index is what orders them - which is the only
        // information there is.
        var content = "Kop A\n\nBody een.\n\nKop B\n\nBody twee.\n\nKop C\n\nBody drie.";

        var result = HeadingLocator.Locate(
            content, [NoOffset("Kop A"), NoOffset("Kop B"), NoOffset("Kop C")], OnePage(content));

        Assert.AreEqual(3, result.HeadingsWithoutOffset);
        CollectionAssert.AreEqual(
            new[] { "Kop A", "Kop B", "Kop C" },
            result.Sections.Select(s => s.HeadingText).ToArray());
    }

    [TestMethod]
    public void ARunOfCarriedOffsets_StaysBehindTheHeadingWhoseOffsetItBorrowed()
    {
        // Index is the final tie-break, so several headings sharing one carried offset keep
        // their arrival order among themselves rather than being reshuffled by the sort.
        var content =
            "Kop een\n\nBody een.\n\nKop twee\n\nBody twee.\n\nKop drie\n\nBody drie.\n\nKop vier\n\nBody vier.";

        var result = HeadingLocator.Locate(
            content,
            [H("Kop een", 50), NoOffset("Kop twee"), NoOffset("Kop drie"), H("Kop vier", 900)],
            OnePage(content));

        CollectionAssert.AreEqual(
            new[] { "Kop een", "Kop twee", "Kop drie", "Kop vier" },
            result.Sections.Select(s => s.HeadingText).ToArray());
        Assert.AreEqual(2, result.HeadingsWithoutOffset);
    }

    [TestMethod]
    public void EqualOffsets_AreBrokenByPageNumber()
    {
        // Two headings that state the same offset are ordered by the page they were found on -
        // the second independent key, and the one PageNumber is right for.
        var content = "Kop op pagina een\n\nBody een.\n\nKop op pagina twee\n\nBody twee.";
        var spans   = new PageSpan[]
        {
            new(1, 0, 30, null, false),
            new(2, 30, content.Length - 30, null, false),
        };

        var result = HeadingLocator.Locate(
            content,
            [H("Kop op pagina twee", 500, page: 2), H("Kop op pagina een", 500, page: 1)],
            spans);

        CollectionAssert.AreEqual(
            new[] { "Kop op pagina een", "Kop op pagina twee" },
            result.Sections.Select(s => s.HeadingText).ToArray());
    }

    [TestMethod]
    public void HeadingsArrivingOutOfOrder_AreSortedByOffsetBeforeAnythingIsLocated()
    {
        // The sort is a re-assertion, not a repair - GetHeadingsHelper's forward walk already
        // delivers reading order, measured at 1,273 headings with zero out of order. It stays
        // so the strategy does not depend on an upstream guarantee nothing states.
        var content = "Eerste kop\n\nBody een.\n\nTweede kop\n\nBody twee.";

        var result = HeadingLocator.Locate(
            content, [H("Tweede kop", 900), H("Eerste kop", 100)], OnePage(content));

        CollectionAssert.AreEqual(
            new[] { "Eerste kop", "Tweede kop" },
            result.Sections.Select(s => s.HeadingText).ToArray());
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
            [H("Artikel 1:6 (vacant)", 0), H("Artikel 1:7 Toepassing CAO op relatiepartner", 22)],
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
            [H("Artikel 8 Begrippen", 0), H("Artikel 9 Reikwijdte", 21)],
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
            [H("3.3 Wat moet je doen", 0), H("3.4 Wie is verantwoordelijk", 22)],
            OnePage(content));

        Assert.AreEqual(0, result.PairedHeadingsMerged);
        Assert.AreEqual(2, result.Sections.Count);
    }

    // The gate must not swallow the case the merge exists for. A heading and its continuation
    // are at DIFFERENT levels (or the continuation has no shape at all), so the pair still
    // merges - this is PairedZeroBodyHeadings_AreMergedIntoOneSection's premise, restated here
    // against a shaped first heading to prove the new check is what decides it.
    [TestMethod]
    public void AShapedHeadingFollowedByAnUnshapedContinuation_StillMerges()
    {
        var content = "3.3 Wat moet je doen\n\nActies als het misgaat\n\nDe echte inhoud staat hier.";
        var result  = HeadingLocator.Locate(
            content,
            [H("3.3 Wat moet je doen", 0), H("Acties als het misgaat", 22)],
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
            [H("Hoofdstuk 1 De arbeidsovereenkomst", 0), H("Artikel 1 de arbeidsovereenkomst", 36)],
            OnePage(content));

        Assert.AreEqual(1, result.PairedHeadingsMerged);
    }
}
