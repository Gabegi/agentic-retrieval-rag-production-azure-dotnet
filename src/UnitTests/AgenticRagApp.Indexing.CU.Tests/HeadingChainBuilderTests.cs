using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Utils;

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

    // ── Sibling and vacant filtering (260818: 479 paths with 2+ Artikel levels) ─────────────

    [TestMethod]
    public void AnArtikelAncestorOfAnArtikelLeaf_IsASibling_NotAParent()
    {
        // The measured CAO shape: DI over-extends Artikel 14's span across the articles after
        // it, and containment alone reads "Artikel 14 > Artikel 16". Same structural level
        // means sibling, whatever the spans say.
        var headings = new[]
        {
            H("Hoofdstuk 2 Vakantie", 0),
            H("Artikel 14 inleveren van vakantie-uren", 100),
            H("Artikel 16 arbeidsongeschiktheid tijdens vakantie", 300),
        };
        var sections = new[] { S(0, 900), S(100, 700), S(300, 100) };

        var chains = HeadingChainBuilder.Build(sections, headings);

        Assert.AreEqual("Hoofdstuk 2 Vakantie > Artikel 16 arbeidsongeschiktheid tijdens vakantie",
            HeadingChainBuilder.Path(chains, 300, "Artikel 16 arbeidsongeschiktheid tijdens vakantie"));
    }

    [TestMethod]
    public void ColonNumberedArtikelen_AreSiblingsToo()
    {
        // CAO GHZ numbering: "Artikel 4:5" swallowing "Artikel 4:10" via an over-extended span.
        var headings = new[]
        {
            H("Hoofdstuk 3 Algemene verplichtingen", 0),
            H("Artikel 4:5 (vacant)", 100),
            H("Artikel 4:10 Vakantietoeslag", 200),
        };
        var sections = new[] { S(0, 900), S(100, 500), S(200, 100) };

        var chains = HeadingChainBuilder.Build(sections, headings);

        Assert.AreEqual("Hoofdstuk 3 Algemene verplichtingen > Artikel 4:10 Vakantietoeslag",
            HeadingChainBuilder.Path(chains, 200, "Artikel 4:10 Vakantietoeslag"));
    }

    [TestMethod]
    public void AVacantHeading_IsNeverAnAncestor()
    {
        // "(vacant)" has no content, so nothing can genuinely sit under it - and with the
        // chain capped to its last three levels, every false segment evicts a real one.
        var headings = new[]
        {
            H("Bijlage V FWG-reglement", 0),
            H("Artikel 3:3 (vacant)", 100),
            H("Salarisschaal functiegroep 25", 200),
        };
        var sections = new[] { S(0, 900), S(100, 500), S(200, 100) };

        var chains = HeadingChainBuilder.Build(sections, headings);

        Assert.AreEqual("Bijlage V FWG-reglement > Salarisschaal functiegroep 25",
            HeadingChainBuilder.Path(chains, 200, "Salarisschaal functiegroep 25"));
    }

    [TestMethod]
    public void GenuineNesting_SurvivesTheSiblingFilter()
    {
        // Different shapes really do nest: Hoofdstuk over Artikel, "1." over "1.1". The filter
        // must only reject SAME-level containment.
        var headings = new[]
        {
            H("Hoofdstuk 3", 0),
            H("Artikel 3:5 Vergoeding schade", 100),
            H("1. Inleiding", 400),
            H("1.1. Doelstelling", 450),
        };
        var sections = new[] { S(0, 900), S(100, 200), S(400, 300), S(450, 100) };

        var chains = HeadingChainBuilder.Build(sections, headings);

        Assert.AreEqual("Hoofdstuk 3 > Artikel 3:5 Vergoeding schade",
            HeadingChainBuilder.Path(chains, 100, "Artikel 3:5 Vergoeding schade"));
        Assert.AreEqual("Hoofdstuk 3 > 1. Inleiding > 1.1. Doelstelling",
            HeadingChainBuilder.Path(chains, 450, "1.1. Doelstelling"));
    }

    [TestMethod]
    public void SameDepthDottedNumbers_AreSiblings()
    {
        // "3." containing "4." is the dotted-number version of the Artikel error.
        var headings = new[]
        {
            H("Contoso Privacybeleid", 0),
            H("3. Verantwoordelijkheden", 100),
            H("4. Algemeen beleid gebruik van persoonsgegevens", 300),
        };
        var sections = new[] { S(0, 900), S(100, 700), S(300, 100) };

        var chains = HeadingChainBuilder.Build(sections, headings);

        Assert.AreEqual("Contoso Privacybeleid > 4. Algemeen beleid gebruik van persoonsgegevens",
            HeadingChainBuilder.Path(chains, 300, "4. Algemeen beleid gebruik van persoonsgegevens"));
    }

    [TestMethod]
    public void ALeadingBareNumber_IsNotANumberedHeading()
    {
        // "2024 Jaarplan" opens with a number but is not a "N." heading - treating it as one
        // discarded its genuine numbered parent as a sibling (code-review finding, 260818).
        var headings = new[] { H("3. Plannen", 0), H("2024 Jaarplan", 100) };
        var sections = new[] { S(0, 900), S(100, 100) };

        var chains = HeadingChainBuilder.Build(sections, headings);

        Assert.AreEqual("3. Plannen > 2024 Jaarplan",
            HeadingChainBuilder.Path(chains, 100, "2024 Jaarplan"));
    }

    [TestMethod]
    public void ADocumentSpanningCoverSlogan_IsNotAnAncestor()
    {
        // DI opens a section on the cover page and runs its span to the end of the file, so the
        // CAO VVT slogan became the root of EVERY VVT breadcrumb in the 260818 run - repeated
        // into every VVT chunk's embedded text for nothing.
        var headings = new[]
        {
            H("De client centraal DE MEDEWERKER OP EEN!", 0),
            H("Artikel 3:5 Vergoeding schade", 100),
        };
        var sections = new[] { S(0, 900), S(100, 100) };

        var chains = HeadingChainBuilder.Build(sections, headings);

        Assert.AreEqual("Artikel 3:5 Vergoeding schade",
            HeadingChainBuilder.Path(chains, 100, "Artikel 3:5 Vergoeding schade"));
    }

    [TestMethod]
    public void ADocumentSpanningPlainTitle_IsStillAnAncestor()
    {
        // The span and the missing numbering are the SAME for a real document title used as a
        // root, so neither can be the deciding signal - only the slogan typography separates
        // them. "Contoso Privacybeleid" keeps its place in the chain.
        var headings = new[] { H("Contoso Privacybeleid", 0), H("3. Verantwoordelijkheden", 100) };
        var sections = new[] { S(0, 900), S(100, 100) };

        var chains = HeadingChainBuilder.Build(sections, headings);

        Assert.AreEqual("Contoso Privacybeleid > 3. Verantwoordelijkheden",
            HeadingChainBuilder.Path(chains, 100, "3. Verantwoordelijkheden"));
    }

    [TestMethod]
    public void AShoutedHeadingThatDoesNotSpanTheDocument_IsStillAnAncestor()
    {
        // All three conditions are required. A shouted heading over a narrow span is a real
        // section ("LET OP!" over a warning block), not cover furniture.
        var headings = new[] { H("LET OP GEVAARLIJKE STOFFEN!", 100), H("Opslag", 150) };
        var sections = new[] { S(0, 900), S(100, 200), S(150, 20) };

        var chains = HeadingChainBuilder.Build(sections, headings);

        Assert.AreEqual("LET OP GEVAARLIJKE STOFFEN! > Opslag",
            HeadingChainBuilder.Path(chains, 150, "Opslag"));
    }

    [TestMethod]
    public void ATwoWordAcronymTitle_IsNotShouting()
    {
        // This corpus's document titles ARE two shouted words, which is exactly why the run
        // threshold is three. "CAO GGZ" is the root most worth keeping, not a slogan.
        var headings = new[] { H("CAO GGZ", 0), H("Artikel 4:10 Vakantietoeslag", 100) };
        var sections = new[] { S(0, 900), S(100, 100) };

        var chains = HeadingChainBuilder.Build(sections, headings);

        Assert.AreEqual("CAO GGZ > Artikel 4:10 Vakantietoeslag",
            HeadingChainBuilder.Path(chains, 100, "Artikel 4:10 Vakantietoeslag"));
    }

    [TestMethod]
    public void UnshapedHeadings_KeepTheContainmentVerdict()
    {
        // Two headings with no recognisable numbering ("Inleiding" over "Definities") say
        // nothing about levels either way - the filter must not touch them.
        var headings = new[] { H("Inleiding", 0), H("Definities", 100) };
        var sections = new[] { S(0, 900), S(100, 100) };

        var chains = HeadingChainBuilder.Build(sections, headings);

        Assert.AreEqual("Inleiding > Definities",
            HeadingChainBuilder.Path(chains, 100, "Definities"));
    }
}
