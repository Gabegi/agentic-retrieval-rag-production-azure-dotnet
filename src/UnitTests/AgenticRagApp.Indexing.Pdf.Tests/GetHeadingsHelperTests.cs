using System.ClientModel.Primitives;
using Azure.AI.DocumentIntelligence;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using AgenticRagApp.Indexing.Pdf.Services;

namespace RagApp.UnitTests.PdfExtraction;

[TestClass]
public class GetHeadingsHelperTests
{
    // Builds a real Azure.AI.DocumentIntelligence.AnalyzeResult with a
    // hand-written paragraphs array, via ModelReaderWriter - same construction
    // path as PdfDocumentAnalyzerTests.SinglePageResult, extended to populate
    // Paragraphs (every existing test in that file uses an empty array).
    // Role is omitted from the JSON (rather than sent as null) when not given,
    // matching how DI never sends a role for a plain body paragraph.
    private static AnalyzeResult ResultWithParagraphs(params (string? Role, string Content)[] paragraphs)
    {
        var paragraphsJson = string.Join(",", paragraphs.Select(p =>
        {
            var roleJson = p.Role is null ? "" : $$""", "role": "{{p.Role}}" """;
            return $$"""
            { "content": "{{Escape(p.Content)}}"{{roleJson}},
              "boundingRegions": [ { "pageNumber": 1, "polygon": [] } ],
              "spans": [ { "offset": 0, "length": {{p.Content.Length}} } ] }
            """;
        }));

        var json = $$"""
        {
          "apiVersion": "2024-11-30",
          "modelId": "prebuilt-layout",
          "content": "placeholder",
          "contentFormat": "markdown",
          "pages": [
            { "pageNumber": 1, "words": [], "lines": [], "selectionMarks": [], "spans": [ { "offset": 0, "length": 11 } ] }
          ],
          "paragraphs": [ {{paragraphsJson}} ], "tables": [], "figures": [], "sections": [], "warnings": []
        }
        """;

        return ModelReaderWriter.Read<AnalyzeResult>(BinaryData.FromString(json))!;
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");

    // Depth-focused fixture: unlike ResultWithParagraphs above, this one makes Content and
    // each paragraph's offset agree with each other, since ComputeDepth reads the "#" run in
    // Content immediately before a paragraph's own offset - the placeholder/offset:0 shape
    // every other test in this file uses can't exercise that at all.
    private static AnalyzeResult ResultWithContentAndParagraphs(string content, params (string? Role, string Text, int Offset)[] paragraphs)
    {
        var paragraphsJson = string.Join(",", paragraphs.Select(p =>
        {
            var roleJson = p.Role is null ? "" : $$""", "role": "{{p.Role}}" """;
            return $$"""
            { "content": "{{Escape(p.Text)}}"{{roleJson}},
              "boundingRegions": [ { "pageNumber": 1, "polygon": [] } ],
              "spans": [ { "offset": {{p.Offset}}, "length": {{p.Text.Length}} } ] }
            """;
        }));

        var json = $$"""
        {
          "apiVersion": "2024-11-30",
          "modelId": "prebuilt-layout",
          "content": "{{Escape(content)}}",
          "contentFormat": "markdown",
          "pages": [
            { "pageNumber": 1, "words": [], "lines": [], "selectionMarks": [], "spans": [ { "offset": 0, "length": {{content.Length}} } ] }
          ],
          "paragraphs": [ {{paragraphsJson}} ], "tables": [], "figures": [], "sections": [], "warnings": []
        }
        """;

        return ModelReaderWriter.Read<AnalyzeResult>(BinaryData.FromString(json))!;
    }

    // --- Core merge behaviour ---------------------------------------------

    [TestMethod]
    public void TwoLineArtikelAndTerm_MergesIntoOneHeading()
    {
        var result = ResultWithParagraphs(("sectionHeading", "Artikel 9"), (null, "opleiding"));

        var headings = GetHeadingsHelper.GetHeadings(result).Headings;

        Assert.AreEqual(1, headings.Count);
        Assert.AreEqual("Artikel 9 opleiding", headings[0].Content);
    }

    [TestMethod]
    public void SingleLineHeadingWithTitleText_IsUntouched()
    {
        var result = ResultWithParagraphs(("sectionHeading", "Artikel 1 doel van de opleiding"));

        var headings = GetHeadingsHelper.GetHeadings(result).Headings;

        Assert.AreEqual(1, headings.Count);
        Assert.AreEqual("Artikel 1 doel van de opleiding", headings[0].Content);
    }

    [TestMethod]
    public void BareLabelFollowedByAnotherHeading_NeitherMerges()
    {
        var result = ResultWithParagraphs(("sectionHeading", "Artikel 8"), ("sectionHeading", "Artikel 9"));

        var headings = GetHeadingsHelper.GetHeadings(result).Headings;

        Assert.AreEqual(2, headings.Count);
        Assert.AreEqual("Artikel 8", headings[0].Content);
        Assert.AreEqual("Artikel 9", headings[1].Content);
    }

    [TestMethod]
    public void BareLabelFollowedByLongPunctuatedProse_StaysUnmerged()
    {
        // The real p.99 shape: an article with no short title, whose "definition"
        // starts directly as a full sentence - a legitimate orphan, not a miss.
        var result = ResultWithParagraphs(
            ("sectionHeading", "Artikel 5"),
            (null, "De formele en materiële verantwoordelijkheden en de daarbij behorende bevoegdheden van de bij de opleiding betrokken personen."));

        var headings = GetHeadingsHelper.GetHeadings(result).Headings;

        Assert.AreEqual(1, headings.Count);
        Assert.AreEqual("Artikel 5", headings[0].Content);
    }

    [TestMethod]
    public void BareLabelAtEndOfList_DoesNotThrow()
    {
        var result = ResultWithParagraphs(("sectionHeading", "Bijlage XII"));

        var headings = GetHeadingsHelper.GetHeadings(result).Headings;

        Assert.AreEqual(1, headings.Count);
        Assert.AreEqual("Bijlage XII", headings[0].Content);
    }

    [TestMethod]
    public void EmptyNextParagraph_DoesNotMerge_NoTrailingSpace()
    {
        var result = ResultWithParagraphs(("sectionHeading", "Artikel 9"), (null, ""));

        var headings = GetHeadingsHelper.GetHeadings(result).Headings;

        Assert.AreEqual(1, headings.Count);
        Assert.AreEqual("Artikel 9", headings[0].Content);
    }

    // --- D2: paired zero-body headings ------------------------------------

    [TestMethod]
    public void TopicHeadingImmediatelyFollowedByActiesHeading_MergesIntoOne()
    {
        var result = ResultWithParagraphs(
            ("sectionHeading", "3.3 Wat moet je doen als iets fout gaat?"),
            ("sectionHeading", "Acties als iets mis gaat"));

        var headings = GetHeadingsHelper.GetHeadings(result).Headings;

        Assert.AreEqual(1, headings.Count);
        Assert.AreEqual("3.3 Wat moet je doen als iets fout gaat?\nActies als iets mis gaat", headings[0].Content);
    }

    [TestMethod]
    public void ThreeHeadingsInARowWithZeroBody_AllMergeIntoOne()
    {
        var result = ResultWithParagraphs(
            ("sectionHeading", "Topic"),
            ("sectionHeading", "Middle"),
            ("sectionHeading", "Acties"));

        var headings = GetHeadingsHelper.GetHeadings(result).Headings;

        Assert.AreEqual(1, headings.Count);
        Assert.AreEqual("Topic\nMiddle\nActies", headings[0].Content);
    }

    [TestMethod]
    public void PairedHeadingWithBodyBetween_DoesNotMerge()
    {
        var result = ResultWithParagraphs(
            ("sectionHeading", "3.3 Wat moet je doen als iets fout gaat?"),
            (null, "Some ordinary body prose sits here."),
            ("sectionHeading", "Acties als iets mis gaat"));

        var headings = GetHeadingsHelper.GetHeadings(result).Headings;

        Assert.AreEqual(2, headings.Count);
        Assert.AreEqual("3.3 Wat moet je doen als iets fout gaat?", headings[0].Content);
        Assert.AreEqual("Acties als iets mis gaat", headings[1].Content);
    }

    [TestMethod]
    public void PairedHeadingMerge_UsesFirstHeadingsOffsetAndPage()
    {
        var result = ResultWithParagraphs(
            ("sectionHeading", "Topic"),
            ("sectionHeading", "Acties"));

        var headings = GetHeadingsHelper.GetHeadings(result).Headings;

        Assert.AreEqual(0, headings[0].Offset);
        Assert.AreEqual(1, headings[0].PageNumber);
    }

    [TestMethod]
    public void PairedHeadingMerge_IsRecordedInPairedHeadingMerges()
    {
        var result = ResultWithParagraphs(
            ("sectionHeading", "Topic"),
            ("sectionHeading", "Acties"));

        var merges = GetHeadingsHelper.GetHeadings(result).PairedHeadingMerges;

        Assert.AreEqual(1, merges.Count);
        Assert.AreEqual("Topic", merges[0]);
    }

    [TestMethod]
    public void VocabularySurvivesASuccessfulMerge()
    {
        var result = ResultWithParagraphs(("sectionHeading", "Artikel 9"), (null, "opleiding"));

        var labels = GetHeadingsHelper.GetHeadings(result).NumberedLabelsSeen;

        Assert.AreEqual(1, labels["Artikel"]);
    }

    [TestMethod]
    public void EnglishLabel_MatchesShapeTooAndMerges()
    {
        var result = ResultWithParagraphs(("sectionHeading", "Article 9"), (null, "training"));

        var headingsResult = GetHeadingsHelper.GetHeadings(result);

        Assert.AreEqual("Article 9 training", headingsResult.Headings[0].Content);
        Assert.AreEqual(1, headingsResult.NumberedLabelsSeen["Article"]);
    }

    // --- Boundary conditions on the merge heuristic ------------------------

    [TestMethod]
    public void NextParagraphExactly60Chars_Merges()
    {
        var term60 = new string('a', 60);
        var result = ResultWithParagraphs(("sectionHeading", "Artikel 9"), (null, term60));

        var headings = GetHeadingsHelper.GetHeadings(result).Headings;

        Assert.AreEqual($"Artikel 9 {term60}", headings[0].Content);
    }

    [TestMethod]
    public void NextParagraph61Chars_DoesNotMerge()
    {
        var term61 = new string('a', 61);
        var result = ResultWithParagraphs(("sectionHeading", "Artikel 9"), (null, term61));

        var headings = GetHeadingsHelper.GetHeadings(result).Headings;

        Assert.AreEqual(1, headings.Count);
        Assert.AreEqual("Artikel 9", headings[0].Content);
    }

    [TestMethod]
    [DataRow(".")]
    [DataRow(":")]
    [DataRow("?")]
    [DataRow(";")]
    [DataRow("!")]
    public void NextParagraphEndingInTerminalPunctuation_DoesNotMerge(string punctuation)
    {
        var result = ResultWithParagraphs(("sectionHeading", "Artikel 9"), (null, $"opleiding{punctuation}"));

        var headings = GetHeadingsHelper.GetHeadings(result).Headings;

        Assert.AreEqual(1, headings.Count);
        Assert.AreEqual("Artikel 9", headings[0].Content);
    }

    [TestMethod]
    public void WhitespaceOnlyNextParagraph_DoesNotMerge()
    {
        var result = ResultWithParagraphs(("sectionHeading", "Artikel 9"), (null, "   "));

        var headings = GetHeadingsHelper.GetHeadings(result).Headings;

        Assert.AreEqual(1, headings.Count);
        Assert.AreEqual("Artikel 9", headings[0].Content);
    }

    [TestMethod]
    public void EmptyHeadingParagraphContent_DoesNotThrow_NotACandidate()
    {
        var result = ResultWithParagraphs(("sectionHeading", ""), (null, "opleiding"));

        var headings = GetHeadingsHelper.GetHeadings(result).Headings;

        Assert.AreEqual(1, headings.Count);
        Assert.AreEqual("", headings[0].Content);
    }

    // --- Vocabulary / shape coverage ----------------------------------------

    [TestMethod]
    public void HoofdstukLabel_MergesAndIsCounted()
    {
        var result = ResultWithParagraphs(("sectionHeading", "Hoofdstuk 4"), (null, "Sociaal beleid"));

        var headingsResult = GetHeadingsHelper.GetHeadings(result);

        Assert.AreEqual("Hoofdstuk 4 Sociaal beleid", headingsResult.Headings[0].Content);
        Assert.AreEqual(1, headingsResult.NumberedLabelsSeen["Hoofdstuk"]);
    }

    [TestMethod]
    public void BijlageRomanNumeralLabel_MergesAndIsCounted()
    {
        var result = ResultWithParagraphs(("sectionHeading", "Bijlage XII"), (null, "Opleidingsovereenkomst"));

        var headingsResult = GetHeadingsHelper.GetHeadings(result);

        Assert.AreEqual("Bijlage XII Opleidingsovereenkomst", headingsResult.Headings[0].Content);
        Assert.AreEqual(1, headingsResult.NumberedLabelsSeen["Bijlage"]);
    }

    [TestMethod]
    public void DottedSubNumbering_MatchesShapeAndMerges()
    {
        var result = ResultWithParagraphs(("sectionHeading", "Section 3.2"), (null, "Scope"));

        var headings = GetHeadingsHelper.GetHeadings(result).Headings;

        Assert.AreEqual("Section 3.2 Scope", headings[0].Content);
    }

    [TestMethod]
    public void LowercaseLabel_StillMatchesShapeAndMerges()
    {
        var result = ResultWithParagraphs(("sectionHeading", "artikel 9"), (null, "opleiding"));

        var headings = GetHeadingsHelper.GetHeadings(result).Headings;

        Assert.AreEqual("artikel 9 opleiding", headings[0].Content);
    }

    [TestMethod]
    public void LooseRomanNumeralBranch_MatchesNonNumeralLetterRun_KnownLimitation()
    {
        // BareNumberedLabelWithWord matches any run of IVXLCDM letters, not just
        // valid roman numerals - documented as a known, accepted limitation
        // (zero real cases in the corpus scan). This pins current behaviour so a
        // future change to the regex is a deliberate decision, not a silent one.
        var result = ResultWithParagraphs(("sectionHeading", "Bijlage CIVIL"), (null, "Voorbeeld"));

        var headings = GetHeadingsHelper.GetHeadings(result).Headings;

        Assert.AreEqual("Bijlage CIVIL Voorbeeld", headings[0].Content);
    }

    [TestMethod]
    public void MultipleDistinctLabels_AreCountedIndependently()
    {
        var result = ResultWithParagraphs(
            ("sectionHeading", "Artikel 9"), (null, "opleiding"),
            ("sectionHeading", "Artikel 10"), (null, "stagiair"),
            ("sectionHeading", "Hoofdstuk 4"), (null, "Sociaal beleid"));

        var labels = GetHeadingsHelper.GetHeadings(result).NumberedLabelsSeen;

        Assert.AreEqual(2, labels["Artikel"]);
        Assert.AreEqual(1, labels["Hoofdstuk"]);
    }

    // --- Role filtering ------------------------------------------------------

    [TestMethod]
    public void NonHeadingRoleParagraph_NeverBecomesAHeading_EvenIfShapeMatches()
    {
        var result = ResultWithParagraphs((null, "Artikel 9"));

        var headings = GetHeadingsHelper.GetHeadings(result).Headings;

        Assert.AreEqual(0, headings.Count);
    }

    [TestMethod]
    public void TitleRoleParagraph_AlsoMergesWithFollowingTerm()
    {
        // "Bijlage VI", not "Bijlage V": this test is about the Title role merging at all,
        // and a two-character numeral keeps it independent of the single-letter rule
        // exercised by SingleLetterRomanNumeral_DoesNotMatch_SoSiblingLabelsAreTreatedAlike.
        var result = ResultWithParagraphs(("title", "Bijlage VI"), (null, "FWG-reglement"));

        var headings = GetHeadingsHelper.GetHeadings(result).Headings;

        Assert.AreEqual(1, headings.Count);
        Assert.AreEqual("Bijlage VI FWG-reglement", headings[0].Content);
    }

    // --- D3: single-letter roman-numeral tightening -------------------------

    [TestMethod]
    public void SingleLetterRomanNumeral_DoesNotMatch_SoSiblingLabelsAreTreatedAlike()
    {
        // The real corpus case (docs/2608/260811/d3-short-label-discovery-findings.md):
        // "Mobiliteitsklasse A/B/C/D/E" are mobility classes, not numerals. Before the
        // tightening only "C" matched, purely because C is in [IVXLCDM] - one of five
        // identical headings treated differently by letter. All five must now behave
        // the same way: no merge, no orphan/vocabulary signal.
        var result = ResultWithParagraphs(
            ("title", "Mobiliteitsklasse C"), (null, "De C-client heeft hulp nodig"));

        var headingsResult = GetHeadingsHelper.GetHeadings(result);

        Assert.AreEqual(1, headingsResult.Headings.Count);
        Assert.AreEqual("Mobiliteitsklasse C", headingsResult.Headings[0].Content);
        Assert.AreEqual(0, headingsResult.NumberedLabelsSeen.Count);
    }

    [TestMethod]
    [DataRow("Mobiliteitsklasse A")]
    [DataRow("Mobiliteitsklasse B")]
    [DataRow("Mobiliteitsklasse C")]
    [DataRow("Mobiliteitsklasse D")]
    [DataRow("Mobiliteitsklasse E")]
    public void SingleLetterLabels_AllSiblingsBehaveIdentically(string heading)
    {
        var result = ResultWithParagraphs(("title", heading), (null, "beschrijving van de klasse"));

        var headingsResult = GetHeadingsHelper.GetHeadings(result);

        Assert.AreEqual(heading, headingsResult.Headings[0].Content);
        Assert.AreEqual(0, headingsResult.NumberedLabelsSeen.Count);
    }

    [TestMethod]
    public void MultiLetterRomanNumeral_StillMatchesAndMerges()
    {
        // The tightening must not cost real roman-numeral labels.
        var result = ResultWithParagraphs(("sectionHeading", "Hoofdstuk IV"), (null, "Arbeidsduur"));

        var headingsResult = GetHeadingsHelper.GetHeadings(result);

        Assert.AreEqual("Hoofdstuk IV Arbeidsduur", headingsResult.Headings[0].Content);
        Assert.AreEqual(1, headingsResult.NumberedLabelsSeen["Hoofdstuk"]);
    }

    [TestMethod]
    [DataRow("pageHeader")]
    [DataRow("pageFooter")]
    [DataRow("footnote")]
    [DataRow("pageNumber")]
    public void BoilerplateRoleParagraph_NeverConsideredEvenIfShapeMatches(string boilerplateRole)
    {
        var result = ResultWithParagraphs((boilerplateRole, "Artikel 9"));

        var headings = GetHeadingsHelper.GetHeadings(result).Headings;

        Assert.AreEqual(0, headings.Count);
    }

    // --- Sequencing / index-skip correctness ----------------------------------

    [TestMethod]
    public void TwoConsecutiveMergePairs_BothMergeCorrectly_NoOffByOne()
    {
        // Mirrors the real p.71 shape: two back-to-back Artikel N / term pairs.
        var result = ResultWithParagraphs(
            ("sectionHeading", "Artikel 9"), (null, "opleiding"),
            ("sectionHeading", "Artikel 10"), (null, "beroepsvoorbereidende periode"));

        var headings = GetHeadingsHelper.GetHeadings(result).Headings;

        Assert.AreEqual(2, headings.Count);
        Assert.AreEqual("Artikel 9 opleiding", headings[0].Content);
        Assert.AreEqual("Artikel 10 beroepsvoorbereidende periode", headings[1].Content);
    }

    [TestMethod]
    public void MergePairFollowedByBodyParagraph_BodyParagraphNeverBecomesAHeading()
    {
        var result = ResultWithParagraphs(
            ("sectionHeading", "Artikel 9"), (null, "opleiding"),
            (null, "Het geheel van activiteiten gericht op het verwerven van vaardigheden."));

        var headings = GetHeadingsHelper.GetHeadings(result).Headings;

        Assert.AreEqual(1, headings.Count);
        Assert.AreEqual("Artikel 9 opleiding", headings[0].Content);
    }

    // --- Empty / degenerate input ----------------------------------------------

    [TestMethod]
    public void NoParagraphs_ReturnsEmptyHeadingsAndEmptyVocabulary()
    {
        var result = ResultWithParagraphs();

        var headingsResult = GetHeadingsHelper.GetHeadings(result);

        Assert.AreEqual(0, headingsResult.Headings.Count);
        Assert.AreEqual(0, headingsResult.NumberedLabelsSeen.Count);
    }

    [TestMethod]
    public void OnlyBodyRoleParagraphs_ReturnsZeroHeadingsAndZeroLabels()
    {
        var result = ResultWithParagraphs((null, "Some regular body text."), (null, "More body text."));

        var headingsResult = GetHeadingsHelper.GetHeadings(result);

        Assert.AreEqual(0, headingsResult.Headings.Count);
        Assert.AreEqual(0, headingsResult.NumberedLabelsSeen.Count);
    }

    // --- Depth (A6) -----------------------------------------------------------

    [TestMethod]
    public void SectionHeading_TwoHashRun_IsDepth2()
    {
        const string content = "## Section Heading\n\nBody text.";
        var result = ResultWithContentAndParagraphs(content, ("sectionHeading", "Section Heading", 3));

        var headings = GetHeadingsHelper.GetHeadings(result).Headings;

        Assert.AreEqual(2, headings[0].Depth);
    }

    [TestMethod]
    public void SectionHeading_FourHashRun_IsDepth4()
    {
        const string content = "#### Deep Heading\n\nBody.";
        var result = ResultWithContentAndParagraphs(content, ("sectionHeading", "Deep Heading", 5));

        var headings = GetHeadingsHelper.GetHeadings(result).Headings;

        Assert.AreEqual(4, headings[0].Depth);
    }

    [TestMethod]
    public void SectionHeading_NoHashRunBeforeOffset_DefaultsToDepth1()
    {
        // Real corpus shape (docs/2608/260810/validation/hygienecode-pages.json, page 3): a
        // bare numbered TOC entry carries the sectionHeading role without DI rendering it as
        // ATX markdown at all - there's no "#" run to find, not an out-of-range offset.
        const string content = "Intro line.\n1. Voorwoord";
        var result = ResultWithContentAndParagraphs(content, ("sectionHeading", "1.", 12));

        var headings = GetHeadingsHelper.GetHeadings(result).Headings;

        Assert.AreEqual(1, headings[0].Depth);
    }

    [TestMethod]
    public void TitleRole_IsAlwaysDepth1_EvenWithAHashRunBeforeIt()
    {
        // Proves the Title short-circuit wins over the scan: a "##" run sits immediately
        // before this offset too (same shape as SectionHeading_TwoHashRun_IsDepth2), so a
        // depth of 2 here would mean the role check isn't actually short-circuiting.
        const string content = "## Title Text";
        var result = ResultWithContentAndParagraphs(content, ("title", "Title Text", 3));

        var headings = GetHeadingsHelper.GetHeadings(result).Headings;

        Assert.AreEqual(1, headings[0].Depth);
    }

    [TestMethod]
    public void PlaceholderContentFixture_OffsetZero_DefaultsToDepth1()
    {
        // Every other test in this file uses ResultWithParagraphs, whose offsets don't
        // correspond to real positions in "content": "placeholder" - pins that this still
        // degrades safely to the default rather than reading garbage.
        var result = ResultWithParagraphs(("sectionHeading", "Artikel 9"), (null, "opleiding"));

        var headings = GetHeadingsHelper.GetHeadings(result).Headings;

        Assert.AreEqual(1, headings[0].Depth);
    }
}
