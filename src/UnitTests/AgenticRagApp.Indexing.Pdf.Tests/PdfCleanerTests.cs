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
}
