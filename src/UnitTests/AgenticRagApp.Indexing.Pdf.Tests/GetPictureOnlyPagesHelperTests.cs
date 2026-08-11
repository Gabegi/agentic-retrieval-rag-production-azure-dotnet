using System.ClientModel.Primitives;
using Azure.AI.DocumentIntelligence;
using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Indexing.Pdf.Services;

namespace RagApp.UnitTests.PdfExtraction;

[TestClass]
public class GetPictureOnlyPagesHelperTests
{
    // Two pages: page 1 has zero words (a candidate picture-only page), page 2 has one
    // word (ordinary text page) - same minimal real-AnalyzeResult construction as
    // GetHeadingsHelperTests.ResultWithParagraphs, just varying Words instead of Paragraphs.
    private static AnalyzeResult TwoPageResult()
    {
        const string json = """
        {
          "apiVersion": "2024-11-30",
          "modelId": "prebuilt-layout",
          "content": "placeholder",
          "contentFormat": "markdown",
          "pages": [
            { "pageNumber": 1, "words": [], "lines": [], "selectionMarks": [], "spans": [ { "offset": 0, "length": 0 } ] },
            { "pageNumber": 2, "words": [ { "content": "hello", "polygon": [], "span": { "offset": 0, "length": 5 }, "confidence": 1 } ],
              "lines": [], "selectionMarks": [], "spans": [ { "offset": 0, "length": 5 } ] }
          ],
          "paragraphs": [], "tables": [], "figures": [], "sections": [], "warnings": []
        }
        """;

        return ModelReaderWriter.Read<AnalyzeResult>(BinaryData.FromString(json))!;
    }

    private static PdfPageRecord Page(int pageNumber, string content) =>
        new() { BlobName = "doc.pdf", PageNumber = pageNumber, PageContent = content, Title = "doc" };

    private static FigureInfo Figure(int pageNumber) =>
        new(Caption: null, Offset: 0, PageNumber: pageNumber, Id: "1", Elements: []);

    [TestMethod]
    public void MarkPictureOnlyPages_ZeroWordsPlusFigure_IsPictureOnly()
    {
        var pages  = new[] { Page(1, ""), Page(2, "hello world") };
        var result = GetPictureOnlyPagesHelper.MarkPictureOnlyPages(TwoPageResult(), pages, [Figure(1)]);

        Assert.IsTrue(result.Single(p => p.PageNumber == 1).IsPictureOnlyPage);
    }

    [TestMethod]
    public void MarkPictureOnlyPages_ZeroWordsNoFigure_IsNotPictureOnly()
    {
        // Genuinely blank page - zero words, but no figure to explain the blankness.
        var pages  = new[] { Page(1, ""), Page(2, "hello world") };
        var result = GetPictureOnlyPagesHelper.MarkPictureOnlyPages(TwoPageResult(), pages, []);

        Assert.IsFalse(result.Single(p => p.PageNumber == 1).IsPictureOnlyPage);
    }

    [TestMethod]
    public void MarkPictureOnlyPages_FigureOnATextPage_IsNotPictureOnly()
    {
        // Page 2 has words (real text) - a figure alongside real text isn't a picture-only page.
        var pages  = new[] { Page(1, ""), Page(2, "hello world") };
        var result = GetPictureOnlyPagesHelper.MarkPictureOnlyPages(TwoPageResult(), pages, [Figure(2)]);

        Assert.IsFalse(result.Single(p => p.PageNumber == 2).IsPictureOnlyPage);
    }

    [TestMethod]
    public void MarkPictureOnlyPages_EmptyContentDespiteWords_IsPictureOnly()
    {
        // PageContent was cleaned down to nothing (all noise-stripped) even though DI
        // reported words on the underlying page - the EmptyPageContent half of the join,
        // not just ZeroWordsOnPage.
        var pages  = new[] { Page(1, ""), Page(2, "") };
        var result = GetPictureOnlyPagesHelper.MarkPictureOnlyPages(TwoPageResult(), pages, [Figure(2)]);

        Assert.IsTrue(result.Single(p => p.PageNumber == 2).IsPictureOnlyPage);
    }

    [TestMethod]
    public void MarkPictureOnlyPages_PreservesPageOrderAndOtherFields()
    {
        var pages  = new[] { Page(1, ""), Page(2, "hello world") };
        var result = GetPictureOnlyPagesHelper.MarkPictureOnlyPages(TwoPageResult(), pages, [Figure(1)]);

        Assert.AreEqual(2, result.Count);
        Assert.AreEqual("doc.pdf", result[0].BlobName);
        Assert.AreEqual("hello world", result[1].PageContent);
    }
}
