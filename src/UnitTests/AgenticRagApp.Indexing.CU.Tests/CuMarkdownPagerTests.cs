using Azure.AI.ContentUnderstanding;
using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Services;

namespace RagApp.UnitTests.PdfExtraction;

// The page map and the content cleaning, which are the two things that make Content
// Understanding's markdown usable: everything downstream addresses Content by offset, and
// anything left in Content is embedded and searched.
[TestClass]
public class CuMarkdownPagerTests
{
    private static DocumentPage Page(int number, int offset, int length) =>
        ContentUnderstandingModelFactory.DocumentPage(
            pageNumber: number, width: 8.5f, height: 11f,
            spans: [ContentUnderstandingModelFactory.ContentSpan(offset, length)],
            angle: 0f, words: [], lines: [], barcodes: [], formulas: []);

    private static DocumentContent Document(string markdown, params DocumentPage[] pages) =>
        ContentUnderstandingModelFactory.DocumentContent(
            mimeType: "application/pdf", analyzerId: "cap-pdf-layout", category: null, path: null,
            markdown: markdown, fields: null, startPageNumber: 1, endPageNumber: pages.Length,
            unit: LengthUnit.Inch, pages: pages, paragraphs: [], sections: [], tables: [],
            figures: [], annotations: [], hyperlinks: [], segments: []);

    private static (string Content, IReadOnlyList<PageSpan> Spans) Build(
        DocumentContent document, IReadOnlyDictionary<int, bool>? pictureOnly = null) =>
        CuMarkdownPager.BuildContent(document, [], pictureOnly ?? new Dictionary<int, bool>());

    // --- Page metadata comments ------------------------------------------------

    [TestMethod]
    public void PageMetadataComments_AreStripped()
    {
        // All four kinds, including a quoted value. Left in, every one of these is embedded and
        // returned as document text.
        const string markdown =
            "<!-- PageNumber=\"1\" -->\n" +
            "<!-- PageHeader=\"Contoso beleid\" -->\n" +
            "Echte inhoud.\n" +
            "<!-- PageFooter=\"pagina 1 van 3\" -->\n" +
            "<!-- PageBreak -->";

        var (content, _) = Build(Document(markdown, Page(1, 0, markdown.Length)));

        Assert.AreEqual("Echte inhoud.", content);
        StringAssert.DoesNotMatch(content, new System.Text.RegularExpressions.Regex("<!--"));
    }

    [TestMethod]
    public void FigureImage_BecomesItsAltTextAndDescription()
    {
        // The description is the payload the whole migration exists for. The figures/N.M path
        // addresses an endpoint nothing here fetches, so it must not survive into the body.
        const string markdown = "![Stappenplan](figures/1.1 \"Een stroomdiagram met vier stappen.\")";

        var (content, _) = Build(Document(markdown, Page(1, 0, markdown.Length)));

        StringAssert.Contains(content, "Stappenplan");
        StringAssert.Contains(content, "Een stroomdiagram met vier stappen.");
        StringAssert.DoesNotMatch(content, new System.Text.RegularExpressions.Regex(@"figures/"));
    }

    [TestMethod]
    public void FigureWithNoDetectedText_LeavesOnlyTheDescription()
    {
        // CU writes a single space as alt text when it detected nothing in the figure - that
        // space must not become stray whitespace in the body.
        const string markdown = "![ ](figures/2.1 \"Een organogram.\")";

        var (content, _) = Build(Document(markdown, Page(1, 0, markdown.Length)));

        Assert.AreEqual("Een organogram.", content);
    }

    [TestMethod]
    public void StrippingDoesNotLeaveRunsOfBlankLines()
    {
        // A comment alone on its line leaves a hole; two blank lines is the invariant every
        // downstream splitter treats as one paragraph boundary.
        const string markdown = "Eerste alinea.\n\n<!-- PageNumber=\"2\" -->\n\nTweede alinea.";

        var (content, _) = Build(Document(markdown, Page(1, 0, markdown.Length)));

        Assert.AreEqual("Eerste alinea.\n\nTweede alinea.", content);
    }

    [TestMethod]
    public void CharacterRepair_IsNotApplied()
    {
        // Was CharacterRepair_IsApplied, inverted when ExtractedTextRepair was removed. Page
        // bodies now reach the index in whatever form Content Understanding produced - here a
        // decomposed "e" + U+0308, which stays decomposed.
        const string markdown = "Hygie\u0308necode";

        var (content, _) = Build(Document(markdown, Page(1, 0, markdown.Length)));

        Assert.AreEqual(markdown, content);
        Assert.IsTrue(content.Contains('\u0308'), "nothing composes page bodies any more");
    }

    // --- Page spans ------------------------------------------------------------

    [TestMethod]
    public void PageSpans_SliceBackToExactlyTheirOwnPageText()
    {
        // The property everything downstream depends on: ChunkMetadataBuilder resolves a
        // chunk's page by testing its offset against these spans, so a span that does not
        // address its own text mis-cites every chunk on that page.
        const string p1 = "Eerste pagina tekst.";
        const string p2 = "Tweede pagina tekst.";
        var markdown = p1 + "\n<!-- PageBreak -->\n" + p2;

        var document = Document(markdown,
            Page(1, 0, p1.Length),
            Page(2, markdown.Length - p2.Length, p2.Length));

        var (content, spans) = Build(document);

        Assert.AreEqual(2, spans.Count);
        Assert.AreEqual(p1, content.Substring(spans[0].Offset, spans[0].Length));
        Assert.AreEqual(p2, content.Substring(spans[1].Offset, spans[1].Length));
        Assert.AreEqual(1, spans[0].PageNumber);
        Assert.AreEqual(2, spans[1].PageNumber);
    }

    [TestMethod]
    public void EmptyPage_KeepsAZeroLengthSpanAndAddsNoSeparator()
    {
        // The page still gets a span because dropping it would drop its IsPictureOnly flag -
        // the only signal that an otherwise normal document contains diagram pages. And no
        // separator is written around it, because a separator on both sides of nothing is the
        // four-newline run the blank-line collapse exists to prevent.
        const string p1 = "Inhoud.";
        const string p3 = "Meer inhoud.";
        var markdown = p1 + "\n<!-- PageBreak -->\n<!-- PageNumber=\"2\" -->\n<!-- PageBreak -->\n" + p3;

        var document = Document(markdown,
            Page(1, 0, p1.Length),
            Page(2, p1.Length, markdown.Length - p1.Length - p3.Length),
            Page(3, markdown.Length - p3.Length, p3.Length));

        var (content, spans) = Build(document, new Dictionary<int, bool> { [2] = true });

        Assert.AreEqual(3, spans.Count);
        Assert.AreEqual(0, spans[1].Length);
        Assert.IsTrue(spans[1].IsPictureOnly);
        Assert.AreEqual($"{p1}\n\n{p3}", content);
    }

    // --- Segmentation fallbacks ------------------------------------------------

    [TestMethod]
    public void UnusableSpans_FallBackToSplittingOnPageBreak()
    {
        // Page spans are the one part of this mapping never verified against a real response,
        // so an out-of-range or non-monotonic set must not produce a silently wrong page map.
        const string p1 = "Een.";
        const string p2 = "Twee.";
        var markdown = p1 + "\n<!-- PageBreak -->\n" + p2;

        var pages = new[]
        {
            Page(1, 9_999, 5),   // beyond the end of the markdown
            Page(2, 0, 5),
        };

        var segments = CuMarkdownPager.Segment(markdown, pages);

        Assert.AreEqual(2, segments.Count);
        StringAssert.Contains(segments[0].Text, p1);
        StringAssert.Contains(segments[1].Text, p2);
    }

    [TestMethod]
    public void NeitherSpansNorPageBreaks_FailsTheDocumentLoudly()
    {
        // Indexing anyway would give every chunk in the document a guessed page number, which
        // is invisible in the output and wrong in every citation. A failure the operator can
        // see is the only correct outcome.
        const string markdown = "Eén ononderbroken document zonder pagina-markeringen.";

        var pages = new[] { Page(1, 9_999, 5), Page(2, 9_999, 5) };

        var ex = Assert.ThrowsException<CuMarkdownPager.PageSegmentationException>(
            () => CuMarkdownPager.Segment(markdown, pages));

        StringAssert.Contains(ex.Message, "2 page(s) reported");
    }

    [TestMethod]
    public void NoPages_FailsTheDocument()
    {
        var ex = Assert.ThrowsException<CuMarkdownPager.PageSegmentationException>(
            () => CuMarkdownPager.Segment("inhoud", []));

        StringAssert.Contains(ex.Message, "no pages");
    }
}
