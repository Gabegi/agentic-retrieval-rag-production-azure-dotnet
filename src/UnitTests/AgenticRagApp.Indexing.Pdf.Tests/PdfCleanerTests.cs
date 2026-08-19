using Microsoft.VisualStudio.TestTools.UnitTesting;
using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Indexing.Pdf.Services;

namespace RagApp.UnitTests.PdfExtraction;

[TestClass]
public class PdfCleanerTests
{
    private static PdfCleaner BuildCleaner() => new();

    private static PdfPageRecord Page(
        string blobName  = "doc1.pdf",
        int    pageIndex = 0,
        string content   = "Some content",
        string title     = " Title ") => new()
    {
        BlobName    = blobName,
        PageNumber  = pageIndex,
        PageContent = content,
        Title       = title,
    };

    [TestMethod]
    public void ValidPage_IsCleanedAndTrimmed()
    {
        var result = BuildCleaner().CleanPdf([Page()]);

        Assert.AreEqual(1, result.Records.Count);
        Assert.AreEqual(0, result.Errors.Count);
        var record = result.Records[0];
        Assert.AreEqual("Title", record.Title);
    }

    [TestMethod]
    public void HardWrappedProse_IsReflowedIntoOneLine()
    {
        // A PDF hard wrap mid-sentence: line ends on a letter, next starts lowercase. A third
        // of the 260818 corpus's non-blank lines started mid-sentence this way, starving the
        // prose ladder of sentence boundaries.
        var result = BuildCleaner().CleanPdf(
            [Page(content: "De werknemer heeft recht op een\nvergoeding van de kosten.")]);

        Assert.AreEqual("De werknemer heeft recht op een vergoeding van de kosten.",
            result.Records[0].PageContent);
        Assert.AreEqual(1, result.LineWrapsReflowed);
    }

    [TestMethod]
    public void AStrandedNumberedMarker_IsRejoinedToItsClause()
    {
        // 111 lines in the 260818 corpus were a bare "N.". LineWrapReflow cannot repair it -
        // the marker line ends on "." and the clause opens uppercase - and in legal text the
        // article number carries the meaning, so the two must not be indexed apart.
        var result = BuildCleaner().CleanPdf(
            [Page(content: "3.\nKlager en beklaagde kunnen zich laten bijstaan.")]);

        Assert.AreEqual("3. Klager en beklaagde kunnen zich laten bijstaan.",
            result.Records[0].PageContent);
        Assert.AreEqual(1, result.ListMarkersRejoined);
    }

    [TestMethod]
    public void AStrayLeadingPeriod_IsStrippedBeforeTheMarkerRejoins()
    {
        // The other half of the same break: the digits went to their own line and the marker's
        // own "." stayed at the head of the clause. Stripping must happen first, or the rejoin
        // produces "3. . Klager".
        var result = BuildCleaner().CleanPdf(
            [Page(content: "3.\n. Klager en beklaagde kunnen zich laten bijstaan.")]);

        Assert.AreEqual("3. Klager en beklaagde kunnen zich laten bijstaan.",
            result.Records[0].PageContent);
        Assert.AreEqual(2, result.ListMarkersRejoined);
    }

    [TestMethod]
    public void ANumberedMarkerAboveAHeadingOrTable_IsNotRejoined()
    {
        // The lookahead refuses "#" and "|" on purpose: a genuine numbered heading sitting
        // above a rendered heading must not swallow it, and a pipe row is structure TableCutter
        // cuts on.
        const string heading = "3.\n#### Artikel 4:15 Salarisschalen";
        const string table   = "3.\n| a | b |";

        Assert.AreEqual(heading, BuildCleaner().CleanPdf([Page(content: heading)]).Records[0].PageContent);
        Assert.AreEqual(table,   BuildCleaner().CleanPdf([Page(content: table)]).Records[0].PageContent);
        Assert.AreEqual(0,       BuildCleaner().CleanPdf([Page(content: heading)]).ListMarkersRejoined);
    }

    [TestMethod]
    public void ParagraphBreaksListsAndTables_AreNotReflowed()
    {
        // A blank line is a real paragraph break; table rows and list items open with
        // something other than a lowercase letter. None of these may be joined.
        const string content =
            "Eerste alinea eindigt hier\n\ntweede alinea staat los.\n\n| a | b |\n| 1 | 2 |";
        var result = BuildCleaner().CleanPdf([Page(content: content)]);

        Assert.AreEqual(content, result.Records[0].PageContent);
        Assert.AreEqual(0, result.LineWrapsReflowed);
    }

    [TestMethod]
    public void DegreeCelsiusGlyph_IsFoldedInPageBodies()
    {
        // U+2103 survives NFC, so the fold is explicit (ExtractedTextRepair.FoldSymbols) -
        // 788 occurrences in the 260818 index against zero "°C".
        var result = BuildCleaner().CleanPdf([Page(content: "Koel tot 7 ℃ bij ontvangst.")]);

        Assert.AreEqual("Koel tot 7 °C bij ontvangst.", result.Records[0].PageContent);
    }

    [TestMethod]
    public void MarkdownEscapedHyphen_IsUnescaped()
    {
        // Document Intelligence's markdown output escapes a literal "-" mid-sentence as
        // "\-" so it isn't parsed as a list bullet. Left as-is, the indexed text contains
        // a literal backslash that matches neither a plain-text query for "-" nor "\-".
        var result = BuildCleaner().CleanPdf([Page(content: "Section 4\\-2 covers eligibility.")]);

        Assert.AreEqual("Section 4-2 covers eligibility.", result.Records[0].PageContent);
    }

    [TestMethod]
    public void MarkdownEscapedHyphenAtLineBreak_UnescapedBeforeHyphenationRepair()
    {
        // Regression: unescaping must run before LineBreakHyphenation, or an escaped "\-"
        // at a line break never matches that regex's plain "-\n" pattern and the split
        // word ("informa" / "tie") is never rejoined.
        var result = BuildCleaner().CleanPdf([Page(content: "informa\\-\ntie werd verstrekt.")]);

        Assert.AreEqual("informatie werd verstrekt.", result.Records[0].PageContent);
    }

    [TestMethod]
    public void LiteralBackslashHyphen_NotFromMarkdownEscaping_StillUnescaped()
    {
        // CommonMark backslash-escaping is context-free (any ASCII punctuation after a
        // backslash), so this can't distinguish "DI-escaped content" from a source PDF
        // that genuinely contained a literal "\-" - both unescape the same way, matching
        // how DI's own markdown renderer would round-trip it.
        var result = BuildCleaner().CleanPdf([Page(content: "C:\\-drive path")]);

        Assert.AreEqual("C:-drive path", result.Records[0].PageContent);
    }

    [TestMethod]
    public void EmptyContentAfterCleanup_ProducesWarningNotError()
    {
        var result = BuildCleaner().CleanPdf([Page(content: "   ")]);

        Assert.AreEqual(1, result.Records.Count);
        Assert.AreEqual(1, result.Warnings.Count);
        Assert.AreEqual(0, result.Errors.Count);
    }

    [TestMethod]
    public void WellFormedTable_ConvertsToMarkdown_NoFallback()
    {
        var content = "<table><tr><td>a</td><td>b</td></tr><tr><td>c</td><td>d</td></tr></table>";
        var result  = BuildCleaner().CleanPdf([Page(content: content)]);

        StringAssert.Contains(result.Records[0].PageContent, "| a | b |");
        Assert.AreEqual(0, result.TableConversionFallbacks);
        Assert.IsFalse(result.Warnings.Any(w => w.Message.Contains("unparseable shape")));
    }

    [TestMethod]
    public void MalformedTable_NoRows_FallsBackToPlainTextInsteadOfDeletingContent()
    {
        // Regression test for finding #16: a <table> with no <tr> rows used to be deleted
        // outright (ConvertTable returned ""), silently dropping the cell text. It must now
        // fall back to tag-stripped plain text, and the fallback must be counted/warned.
        var content = "<table>important cell text with no rows</table>";
        var result  = BuildCleaner().CleanPdf([Page(content: content)]);

        StringAssert.Contains(result.Records[0].PageContent, "important cell text with no rows");
        Assert.AreEqual(1, result.TableConversionFallbacks);
        Assert.IsTrue(result.Warnings.Any(w => w.Message.Contains("unparseable shape")));
    }

    [TestMethod]
    public void Mojibake_IsRepairedAndCounted()
    {
        var result = BuildCleaner().CleanPdf([Page(content: "GeÃ¯nformeerde beslissing")]);

        StringAssert.Contains(result.Records[0].PageContent, "ï");
        Assert.AreEqual(1, result.MojibakeRepairedPages);
        Assert.IsTrue(result.Warnings.Any(w => w.Message.Contains("mojibake")));
    }

    [TestMethod]
    public void ExcessBlankLines_AreCollapsed()
    {
        var result = BuildCleaner().CleanPdf([Page(content: "Line one\n\n\n\n\nLine two")]);

        Assert.AreEqual("Line one\n\nLine two", result.Records[0].PageContent);
    }

    // PdfCleaner no longer matches a fixed pattern table - it round-trips the whole page
    // through Windows-1252 -> UTF-8 whenever 'Ã'/'â' appear, repairing the entire mis-decode
    // class in one pass. Coverage is round-trip cases, not a pattern-ordering guard, matching
    // PdfCleaner.RepairMojibake's own doc comment.
    //
    // corrupted = the UTF-8 bytes of `expected`, individually reinterpreted as Windows-1252
    // codepoints. E.g. row 1: U+00EF (i-diaeresis) -> UTF-8 bytes 0xC3 0xAF -> read as cp1252
    // codepoints U+00C3 (Ã) and U+00AF (macron) -> "Ã¯". Scoped to this vowel-accent range
    // (the classic Dutch-text mojibake) rather than the smart-quote range (U+2018-U+201D) -
    // those need the exact curly-vs-straight quote character verified byte-for-byte before
    // adding as a test case, not eyeballed.
    [TestMethod]
    [DataRow("Ã¯", "ï", DisplayName = "i-diaeresis (Dutch, e.g. cliënt)")]
    [DataRow("Ã«", "ë", DisplayName = "e-diaeresis (Dutch)")]
    [DataRow("Ã©", "é", DisplayName = "e-acute")]
    [DataRow("Ã¼", "ü", DisplayName = "u-diaeresis")]
    public void KnownMojibakeFragment_RoundTripsToRepairedText(string corrupted, string expected)
    {
        var cleaned = BuildCleaner().CleanPdf([Page(content: $"x {corrupted} y")]).Records[0].PageContent;

        Assert.AreEqual($"x {expected} y", cleaned, $"'{corrupted}' did not repair to '{expected}'.");
    }

    // Safety valve, decode side: 'â' alone is ambiguous - it's both the mojibake fingerprint
    // AND a legitimate letter (e.g. in loanwords). When the round-trip produces a replacement
    // char (invalid UTF-8), that means the source wasn't actually mojibake - keep it as-is.
    // "vâme" = "vâme", a plausible genuine word fragment, not mojibake.
    [TestMethod]
    public void LegitimateAccentedText_IsLeftUntouched()
    {
        var content = "vâme";
        var result  = BuildCleaner().CleanPdf([Page(content: content)]);

        Assert.AreEqual(content, result.Records[0].PageContent);
        Assert.AreEqual(0, result.MojibakeRepairedPages);
    }

    // Safety valve, encode side: the actual bug this class's RepairMojibake was rewritten to
    // fix. A page can contain a genuine mojibake fragment (triggering the round-trip attempt)
    // AND, elsewhere on the same page, a real character outside Windows-1252's repertoire
    // (arrows, checkboxes, non-Latin scripts - here U+2192, a right arrow). The old
    // GetBytes(text) implementation silently replaced that character with '?' and reported
    // "repaired". EncoderExceptionFallback makes that throw instead, so the whole page is
    // left untouched rather than corrupted.
    [TestMethod]
    public void MojibakeFragmentPlusNonCp1252Character_IsLeftUntouched()
    {
        var content = "GeÃ¯nformeerd → volgende stap";
        var result  = BuildCleaner().CleanPdf([Page(content: content)]);

        Assert.AreEqual(content, result.Records[0].PageContent);
        Assert.AreEqual(0, result.MojibakeRepairedPages);
    }

    // --- ConvertFigure ---------------------------------------------------------------

    [TestMethod]
    public void FigureWithFigcaption_UsesCaptionText()
    {
        var content = "Before <figure><figcaption>A diagram of the process</figcaption></figure> after.";
        var result  = BuildCleaner().CleanPdf([Page(content: content)]);

        StringAssert.Contains(result.Records[0].PageContent, "A diagram of the process");
        Assert.IsFalse(result.Records[0].PageContent.Contains("<figure"));
    }

    [TestMethod]
    public void FigureWithOnlyAltText_UsesAltTextWhenNoCaption()
    {
        var content = "Before <figure><img alt=\"A photo of the building\" src=\"x.png\"></figure> after.";
        var result  = BuildCleaner().CleanPdf([Page(content: content)]);

        StringAssert.Contains(result.Records[0].PageContent, "A photo of the building");
    }

    [TestMethod]
    public void FigureWithCaptionAndAltText_PrefersCaptionOverAltText()
    {
        var content = "<figure><figcaption>Caption wins</figcaption><img alt=\"Alt loses\"></figure>";
        var result  = BuildCleaner().CleanPdf([Page(content: content)]);

        StringAssert.Contains(result.Records[0].PageContent, "Caption wins");
        Assert.IsFalse(result.Records[0].PageContent.Contains("Alt loses"));
    }

    [TestMethod]
    public void FigureWithBlankCaption_FallsBackToAltText()
    {
        var content = "<figure><figcaption>   </figcaption><img alt=\"Real alt text\"></figure>";
        var result  = BuildCleaner().CleanPdf([Page(content: content)]);

        StringAssert.Contains(result.Records[0].PageContent, "Real alt text");
    }

    [TestMethod]
    public void FigureWithNeitherCaptionNorAltText_IsStrippedEntirely()
    {
        var content = "Before <figure><img src=\"x.png\"></figure> after.";
        var result  = BuildCleaner().CleanPdf([Page(content: content)]);

        Assert.AreEqual("Before  after.", result.Records[0].PageContent);
    }

    [TestMethod]
    public void FigureWithBlankAltText_IsStrippedEntirely()
    {
        var content = "Before <figure><img alt=\"   \" src=\"x.png\"></figure> after.";
        var result  = BuildCleaner().CleanPdf([Page(content: content)]);

        Assert.AreEqual("Before  after.", result.Records[0].PageContent);
    }

    [TestMethod]
    public void MultipleFiguresOnOnePage_AreEachConvertedIndependently()
    {
        var content =
            "<figure><figcaption>First figure</figcaption></figure> and " +
            "<figure><figcaption>Second figure</figcaption></figure>";
        var result = BuildCleaner().CleanPdf([Page(content: content)]);

        StringAssert.Contains(result.Records[0].PageContent, "First figure");
        StringAssert.Contains(result.Records[0].PageContent, "Second figure");
    }

    [TestMethod]
    public void AWordSplitAcrossABlankLine_IsRejoined()
    {
        // "Contoso" cut in half by a paragraph break inserted inside the word. The 260818 eval
        // saw the second half open a chunk: "daan medewerkers.". LineWrapReflow cannot reach
        // it - that rule needs a single \n - and ExcessBlankLines preserves the gap.
        var result = BuildCleaner().CleanPdf(
            [Page(content: "Zorg dat de Cor\n\ndaan medewerkers hiervan op de hoogte zijn.")]);

        Assert.AreEqual("Zorg dat de Contoso medewerkers hiervan op de hoogte zijn.",
            result.Records[0].PageContent);
        Assert.AreEqual(1, result.LineWrapsReflowed);
    }

    // The repair runs before TrailingLineSpace, so the whitespace debris that line-based
    // extraction leaves around the break is still there when it looks. Both of these were
    // missed by the earlier \n{2,} pattern and left broken in the index.
    [DataTestMethod]
    [DataRow("Zorg dat de Cor \n\ndaan medewerkers zijn.",  DisplayName = "trailing space before the break")]
    [DataRow("Zorg dat de Cor\n \ndaan medewerkers zijn.",  DisplayName = "blank line holding a space")]
    [DataRow("Zorg dat de Cor\n\n  daan medewerkers zijn.", DisplayName = "indented continuation")]
    [DataRow("Zorg dat de Cor\n\n\ndaan medewerkers zijn.", DisplayName = "more than one blank line")]
    public void AWordSplitAcrossAWhitespaceLitteredBreak_IsRejoined(string content)
    {
        var result = BuildCleaner().CleanPdf([Page(content: content)]);

        StringAssert.Contains(result.Records[0].PageContent, "Contoso medewerkers");
        Assert.AreEqual(1, result.LineWrapsReflowed);
    }

    // The split can land anywhere inside the word - the extractor picks the break, not us - so
    // the rule covers every interior cut point of a known token rather than a fixed prefix.
    [DataTestMethod]
    [DataRow("de C\n\nordaan medewerkers")]
    [DataRow("de Cord\n\naan medewerkers")]
    [DataRow("de Cordaa\n\nn medewerkers")]
    public void AKnownWordSplitAtAnyInteriorPoint_IsRejoined(string content)
    {
        var result = BuildCleaner().CleanPdf([Page(content: content)]);

        StringAssert.Contains(result.Records[0].PageContent, "Contoso medewerkers");
    }

    // THE case that forced the rule to be vocabulary rather than shape, and the reason the
    // halves must never be joined on shape alone. Every one of these satisfies the old
    // "capitalised 2-4 letter fragment, lowercase continuation" pattern exactly, and the old
    // rule fused them into "Raadvan", "Wetverbetering" and "Janvan" - silently, before
    // chunking, so the damaged token is what got embedded and cited. "Raad van Bestuur" and
    // "Raad van Toezicht" are everywhere in this corpus.
    [DataTestMethod]
    [DataRow("Meld dit bij de Raad\n\nvan Bestuur van de organisatie.")]
    [DataRow("Zie hiervoor de Wet\n\nverbetering poortwachter.")]
    [DataRow("Dit betreft de heer Jan\n\nvan der Berg.")]
    [DataRow("De zaak diende bij het Hof\n\nvan Justitie.")]
    public void TwoRealWordsAcrossAParagraphBreak_AreNeverFused(string text)
    {
        var result = BuildCleaner().CleanPdf([Page(content: text)]);

        Assert.AreEqual(text, result.Records[0].PageContent);
        Assert.AreEqual(0, result.LineWrapsReflowed);
    }

    // DI emits plenty of short label lines with no markdown marker, and the old lookbehind
    // accepted \n as the separator before the fragment - so a whole heading line could count as
    // the fragment and be glued to the paragraph under it ("Doel" + "het doel" -> "Doelhet").
    [TestMethod]
    public void AnUnmarkedShortHeadingLine_IsNotGluedToTheParagraphBelowIt()
    {
        const string text = "dit is een alinea\nDoel\n\nhet doel is duidelijk.";
        var result = BuildCleaner().CleanPdf([Page(content: text)]);

        Assert.AreEqual(text, result.Records[0].PageContent);
        Assert.AreEqual(0, result.LineWrapsReflowed);
    }

    [TestMethod]
    public void ARealParagraphBreak_IsNotJoined()
    {
        const string twoParagraphs = "De werknemer meldt dit bij de werkgever.\n\nDe werkgever bevestigt de melding.";
        var result = BuildCleaner().CleanPdf([Page(content: twoParagraphs)]);

        Assert.AreEqual(twoParagraphs, result.Records[0].PageContent);
        Assert.AreEqual(0, result.LineWrapsReflowed);
    }

    // A lowercase four-letter word ending a paragraph is an ordinary Dutch paragraph ending -
    // hier, deze, niet, voor. Not on the known-token list, so not touched.
    // ParagraphBreaksListsAndTables_AreNotReflowed is the other half of this guard.
    [TestMethod]
    public void ALowercaseShortWordEndingAParagraph_IsNotTreatedAsAFragment()
    {
        const string text = "De regeling geldt hier\n\nvolgens de geldende afspraken.";
        var result = BuildCleaner().CleanPdf([Page(content: text)]);

        Assert.AreEqual(text, result.Records[0].PageContent);
        Assert.AreEqual(0, result.LineWrapsReflowed);
    }

    // An acronym ending a paragraph is not a fragment either.
    [TestMethod]
    public void AnAcronymEndingAParagraph_IsNotTreatedAsAFragment()
    {
        const string text = "Dit geldt ook in NL\n\nvolgens de geldende afspraken.";
        var result = BuildCleaner().CleanPdf([Page(content: text)]);

        Assert.AreEqual(text, result.Records[0].PageContent);
        Assert.AreEqual(0, result.LineWrapsReflowed);
    }

    [TestMethod]
    public void AFragmentFollowedByAHeadingOrTableRow_IsNotJoined()
    {
        // "#" opens a heading and "|" a table row, and joining either to the line above would
        // destroy the structure the cutters use. Neither half is a known token, so neither is
        // a candidate in the first place.
        var heading = BuildCleaner().CleanPdf([Page(content: "einde van de\n\n## Artikel 3 Vergoedingen")]);
        var table   = BuildCleaner().CleanPdf([Page(content: "einde van de\n\n| trede | bedrag |")]);

        Assert.IsTrue(heading.Records[0].PageContent.Contains("\n\n## Artikel 3"));
        Assert.IsTrue(table.Records[0].PageContent.Contains("\n\n| trede |"));
    }

    [TestMethod]
    public void APunctuatedLineEnd_IsNotAFragment()
    {
        // A sentence that ended properly did not lose a word half.
        const string text = "Dit is het einde.\n\nvervolg van de zin.";
        var result = BuildCleaner().CleanPdf([Page(content: text)]);

        Assert.AreEqual(text, result.Records[0].PageContent);
        Assert.AreEqual(0, result.LineWrapsReflowed);
    }
}
