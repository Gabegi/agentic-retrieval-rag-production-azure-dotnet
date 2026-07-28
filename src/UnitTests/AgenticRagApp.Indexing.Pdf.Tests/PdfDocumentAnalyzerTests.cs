using System.ClientModel.Primitives;
using Azure.AI.DocumentIntelligence;
using Azure.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using AgenticRagApp.Infrastructure.Clients.DocumentIntelligence;
using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Indexing.Pdf.Services;

namespace RagApp.UnitTests.PdfExtraction;

[TestClass]
public class PdfDocumentAnalyzerTests
{
    // PdfDocumentAnalyzer.GetPages/GetPageQuality are instance methods (marked internal for
    // this test's benefit) but never touch _diClient - a Moq stub is enough, it's never invoked.
    private static PdfDocumentIntelligenceAnalyzer BuildAnalyzer() =>
        new(new Mock<IDocumentAnalysisClient>().Object, NullLogger<PdfDocumentIntelligenceAnalyzer>.Instance);

    // Builds a real, single-page Azure.AI.DocumentIntelligence.AnalyzeResult from hand-written
    // JSON via ModelReaderWriter - the SDK's own supported construction path for exactly this
    // (no live service call, no mocking the SDK's model types directly). span length is taken
    // from content.Length, not hand-counted, so it can't drift out of sync with the text.
    private static AnalyzeResult SinglePageResult(string content, IEnumerable<(double Confidence, string Text)>? words = null)
    {
        var wordsJson = string.Join(",", (words ?? []).Select(w =>
            $$"""{ "content": "{{Escape(w.Text)}}", "confidence": {{w.Confidence}}, "span": { "offset": 0, "length": 1 }, "polygon": [] }"""));

        var json = $$"""
        {
          "apiVersion": "2024-11-30",
          "modelId": "prebuilt-layout",
          "content": "{{Escape(content)}}",
          "pages": [
            { "pageNumber": 1, "words": [{{wordsJson}}], "lines": [], "selectionMarks": [], "spans": [ { "offset": 0, "length": {{content.Length}} } ] }
          ],
          "paragraphs": [], "tables": [], "figures": [], "sections": [], "warnings": []
        }
        """;

        return ModelReaderWriter.Read<AnalyzeResult>(BinaryData.FromString(json))!;
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");

    [TestMethod]
    public void EmptyPageContent_ProducesWarning()
    {
        var result = SinglePageResult("");

        var (pages, warnings, _) = BuildAnalyzer().GetPages(result, "doc.pdf", "Title");

        Assert.AreEqual(1, pages.Count);
        Assert.AreEqual("", pages[0].PageContent);
        Assert.IsTrue(warnings.Any(w => w.Code == "EmptyPageContent"));
    }

    [TestMethod]
    public void NonEmptyPageContent_NoEmptyContentWarning()
    {
        var result = SinglePageResult("Some real page text.");

        var (_, warnings, _) = BuildAnalyzer().GetPages(result, "doc.pdf", "Title");

        Assert.IsFalse(warnings.Any(w => w.Code == "EmptyPageContent"));
    }

    [TestMethod]
    public void UnbalancedTableTags_ProducesWarning()
    {
        var result = SinglePageResult("<table><tr><td>a</td></tr>");

        var (_, warnings, _) = BuildAnalyzer().GetPages(result, "doc.pdf", "Title");

        Assert.IsTrue(warnings.Any(w => w.Code == "UnbalancedTableTags"));
    }

    [TestMethod]
    public void BalancedTableTags_NoUnbalancedWarning()
    {
        var result = SinglePageResult("<table><tr><td>a</td></tr></table>");

        var (_, warnings, _) = BuildAnalyzer().GetPages(result, "doc.pdf", "Title");

        Assert.IsFalse(warnings.Any(w => w.Code == "UnbalancedTableTags"));
    }

    [TestMethod]
    public void SetextTitle_IsNormalizedToAtx_AndCounted()
    {
        var result = SinglePageResult("My Title\n===\nBody text.");

        var (pages, _, infos) = BuildAnalyzer().GetPages(result, "doc.pdf", "Title");

        StringAssert.StartsWith(pages[0].PageContent, "# My Title");
        Assert.IsTrue(infos.Any(w => w.Code == "SetextTitleNormalized" && w.Message!.Contains("1 page")));
    }

    [TestMethod]
    public void NoiseComment_IsStrippedAndCounted()
    {
        var content = "<!-- PageHeader=\"Confidential\" -->\nReal content.";
        var result  = SinglePageResult(content);

        var (pages, _, infos) = BuildAnalyzer().GetPages(result, "doc.pdf", "Title");

        Assert.AreEqual("Real content.", pages[0].PageContent);
        Assert.IsTrue(infos.Any(w => w.Code == "NoiseCommentsStripped"));
    }

    [TestMethod]
    public void ZeroWordsOnPage_ProducesWarning()
    {
        var result = SinglePageResult("");

        var warnings = PdfDocumentIntelligenceAnalyzer.GetZeroWordWarnings(result, "doc.pdf");

        Assert.IsTrue(warnings.Any(w => w.Code == "ZeroWordsOnPage"));
    }

    [TestMethod]
    public void PageWithWords_NoZeroWordsWarning()
    {
        var result = SinglePageResult("clean text", [(0.98, "clean"), (0.97, "text")]);

        var warnings = PdfDocumentIntelligenceAnalyzer.GetZeroWordWarnings(result, "doc.pdf");

        Assert.IsFalse(warnings.Any(w => w.Code == "ZeroWordsOnPage"));
    }

    [TestMethod]
    public void FiguresWithoutCaption_ProducesWarning()
    {
        var figures = new[] { new FigureInfo(null, 0, 1, "fig1", []) };

        var warnings = PdfDocumentIntelligenceAnalyzer.StructureWarnings([], figures, blobName: "doc.pdf");

        Assert.IsTrue(warnings.Any(w => w.Code == "FiguresWithoutCaption"));
    }

    [TestMethod]
    public void FiguresWithCaption_NoCaptionWarning()
    {
        var figures = new[] { new FigureInfo("A caption", 0, 1, "fig1", []) };

        var warnings = PdfDocumentIntelligenceAnalyzer.StructureWarnings([], figures, blobName: "doc.pdf");

        Assert.IsFalse(warnings.Any(w => w.Code == "FiguresWithoutCaption"));
    }

    [TestMethod]
    public void MalformedTable_ProducesWarning()
    {
        var tables = new[] { new TableInfo(RowCount: 0, ColumnCount: 0, Cells: [], Offset: 0, PageNumber: 1, Caption: null, Footnotes: [], Regions: []) };

        var warnings = PdfDocumentIntelligenceAnalyzer.StructureWarnings(tables, [], blobName: "doc.pdf");

        Assert.IsTrue(warnings.Any(w => w.Code == "MalformedTable"));
    }

    [TestMethod]
    public void WellFormedTable_NoMalformedWarning()
    {
        var cells  = new[] { new TableCellInfo(0, 0, "content", "a", null, null) };
        var tables = new[] { new TableInfo(RowCount: 1, ColumnCount: 1, Cells: cells, Offset: 0, PageNumber: 1, Caption: null, Footnotes: [], Regions: []) };

        var warnings = PdfDocumentIntelligenceAnalyzer.StructureWarnings(tables, [], blobName: "doc.pdf");

        Assert.IsFalse(warnings.Any(w => w.Code == "MalformedTable"));
    }

    [TestMethod]
    [DataRow(0, 0.00, "0 page(s)")]
    [DataRow(1, 0.01, "1 page(s)")]
    [DataRow(10, 0.10, "10 page(s)")]
    public void CostInfo_EchoesGivenCostAndPageCount(int pageCount, double estimatedCost, string expectedPageCount)
    {
        var info = PdfDocumentIntelligenceAnalyzer.CostInfo((decimal)estimatedCost, pageCount, "doc.pdf");

        Assert.AreEqual("EstimatedCost", info.Code);
        StringAssert.Contains(info.Message, $"${estimatedCost:F2}");
        StringAssert.Contains(info.Message, expectedPageCount);
    }
}
