using System.ClientModel.Primitives;
using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using AgenticRagApp.Infrastructure.Clients.DocumentIntelligence;
using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Services;

namespace RagApp.UnitTests.PdfExtraction;

[TestClass]
public class PdfDocumentAnalyzerTests
{
    // BuildResults/ValidateAnalyzeResult are instance methods (marked internal for this
    // test's benefit) but never touch _diClient - a Moq stub is enough, it's never invoked.
    // GetPages itself now lives in the static GetPagesHelper (DocumentIntelligenceHelpers/).
    private static PdfDocumentIntelligenceAnalyzer BuildAnalyzer() =>
        new(new Mock<IDocumentAnalysisClient>().Object, NullLogger<PdfDocumentIntelligenceAnalyzer>.Instance);

    // Builds a real, single-page Azure.AI.DocumentIntelligence.AnalyzeResult from hand-written
    // JSON via ModelReaderWriter - the SDK's own supported construction path for exactly this
    // (no live service call, no mocking the SDK's model types directly). span length is taken
    // from content.Length, not hand-counted, so it can't drift out of sync with the text.
    private static AnalyzeResult SinglePageResult(
        string content, IEnumerable<(double Confidence, string Text)>? words = null,
        double? width = null, double? height = null, string? unit = null)
    {
        var wordsJson = string.Join(",", (words ?? []).Select(w =>
            $$"""{ "content": "{{Escape(w.Text)}}", "confidence": {{w.Confidence}}, "span": { "offset": 0, "length": 1 }, "polygon": [] }"""));

        // width/height/unit are omitted (not just null) when not given, matching how a
        // real DI response never sends absent fields - GetPageDimensionsHelper's p.Width/
        // p.Height come back null in that case, same as an actual sparse response would.
        var dimensionsJson = width is null
            ? ""
            : $$""", "width": {{width}}, "height": {{height}}, "unit": "{{unit}}" """;

        var json = $$"""
        {
          "apiVersion": "2024-11-30",
          "modelId": "prebuilt-layout",
          "content": "{{Escape(content)}}",
          "contentFormat": "markdown",
          "pages": [
            { "pageNumber": 1, "words": [{{wordsJson}}], "lines": [], "selectionMarks": [], "spans": [ { "offset": 0, "length": {{content.Length}} } ]{{dimensionsJson}} }
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

        var (pages, warnings, _) = GetPagesHelper.GetPages(NullLogger.Instance, result, "doc.pdf", "Title");

        Assert.AreEqual(1, pages.Count);
        Assert.AreEqual("", pages[0].PageContent);
        Assert.IsTrue(warnings.Any(w => w.Code == "EmptyPageContent"));
    }

    [TestMethod]
    public void NonEmptyPageContent_NoEmptyContentWarning()
    {
        var result = SinglePageResult("Some real page text.");

        var (_, warnings, _) = GetPagesHelper.GetPages(NullLogger.Instance, result, "doc.pdf", "Title");

        Assert.IsFalse(warnings.Any(w => w.Code == "EmptyPageContent"));
    }

    [TestMethod]
    public void UnbalancedTableTags_ProducesWarning()
    {
        var result = SinglePageResult("<table><tr><td>a</td></tr>");

        var (_, warnings, _) = GetPagesHelper.GetPages(NullLogger.Instance, result, "doc.pdf", "Title");

        Assert.IsTrue(warnings.Any(w => w.Code == "UnbalancedTableTags"));
    }

    [TestMethod]
    public void BalancedTableTags_NoUnbalancedWarning()
    {
        var result = SinglePageResult("<table><tr><td>a</td></tr></table>");

        var (_, warnings, _) = GetPagesHelper.GetPages(NullLogger.Instance, result, "doc.pdf", "Title");

        Assert.IsFalse(warnings.Any(w => w.Code == "UnbalancedTableTags"));
    }

    [TestMethod]
    public void SetextTitle_IsNormalizedToAtx_AndCounted()
    {
        var result = SinglePageResult("My Title\n===\nBody text.");

        var (pages, _, infos) = GetPagesHelper.GetPages(NullLogger.Instance, result, "doc.pdf", "Title");

        StringAssert.StartsWith(pages[0].PageContent, "# My Title");
        Assert.IsTrue(infos.Any(w => w.Code == "SetextTitleNormalized" && w.Message!.Contains("1 page")));
    }

    [TestMethod]
    public void NoiseComment_IsStrippedAndCounted()
    {
        var content = "<!-- PageHeader=\"Confidential\" -->\nReal content.";
        var result  = SinglePageResult(content);

        var (pages, _, infos) = GetPagesHelper.GetPages(NullLogger.Instance, result, "doc.pdf", "Title");

        Assert.AreEqual("Real content.", pages[0].PageContent);
        Assert.IsTrue(infos.Any(w => w.Code == "NoiseCommentsStripped"));
    }

    [TestMethod]
    public void PageBreakComment_IsStrippedAndCounted()
    {
        var content = "Real content.\n<!-- PageBreak -->";
        var result  = SinglePageResult(content);

        var (pages, _, infos) = GetPagesHelper.GetPages(NullLogger.Instance, result, "doc.pdf", "Title");

        Assert.AreEqual("Real content.", pages[0].PageContent);
        Assert.IsTrue(infos.Any(w => w.Code == "NoiseCommentsStripped"));
    }

    [TestMethod]
    public void UnrecognizedComment_ProducesWarning()
    {
        var content = "Real content.\n<!-- SomeFutureDiComment -->";
        var result  = SinglePageResult(content);

        var (_, warnings, _) = GetPagesHelper.GetPages(NullLogger.Instance, result, "doc.pdf", "Title");

        Assert.IsTrue(warnings.Any(w => w.Code == "UnrecognizedComment"));
    }

    [TestMethod]
    public void ZeroWordsOnPage_ProducesWarning()
    {
        var result = SinglePageResult("");

        var warnings = GetQualityWarningsHelper.GetZeroWordWarnings(result, "doc.pdf");

        Assert.IsTrue(warnings.Any(w => w.Code == "ZeroWordsOnPage"));
    }

    [TestMethod]
    public void PageWithWords_NoZeroWordsWarning()
    {
        var result = SinglePageResult("clean text", [(0.98, "clean"), (0.97, "text")]);

        var warnings = GetQualityWarningsHelper.GetZeroWordWarnings(result, "doc.pdf");

        Assert.IsFalse(warnings.Any(w => w.Code == "ZeroWordsOnPage"));
    }

    [TestMethod]
    public void FiguresWithoutCaption_ProducesWarning()
    {
        var figures = new[] { new FigureInfo(null, 0, 1, "fig1", []) };

        var warnings = GetQualityWarningsHelper.StructureWarnings([], figures, blobName: "doc.pdf");

        Assert.IsTrue(warnings.Any(w => w.Code == "FiguresWithoutCaption"));
    }

    [TestMethod]
    public void FiguresWithCaption_NoCaptionWarning()
    {
        var figures = new[] { new FigureInfo("A caption", 0, 1, "fig1", []) };

        var warnings = GetQualityWarningsHelper.StructureWarnings([], figures, blobName: "doc.pdf");

        Assert.IsFalse(warnings.Any(w => w.Code == "FiguresWithoutCaption"));
    }

    [TestMethod]
    public void MalformedTable_ProducesWarning()
    {
        var tables = new[] { new TableInfo(RowCount: 0, ColumnCount: 0, Cells: [], Offset: 0, PageNumber: 1, Caption: null, Footnotes: [], Regions: []) };

        var warnings = GetQualityWarningsHelper.StructureWarnings(tables, [], blobName: "doc.pdf");

        Assert.IsTrue(warnings.Any(w => w.Code == "MalformedTable"));
    }

    [TestMethod]
    public void WellFormedTable_NoMalformedWarning()
    {
        var cells  = new[] { new TableCellInfo(0, 0, "content", "a", null, null) };
        var tables = new[] { new TableInfo(RowCount: 1, ColumnCount: 1, Cells: cells, Offset: 0, PageNumber: 1, Caption: null, Footnotes: [], Regions: []) };

        var warnings = GetQualityWarningsHelper.StructureWarnings(tables, [], blobName: "doc.pdf");

        Assert.IsFalse(warnings.Any(w => w.Code == "MalformedTable"));
    }

    // --- PageDimensionWarningsHelper -------------------------------------------------

    [TestMethod]
    public void PageDimensions_WithinTolerance_NoWarning()
    {
        var native = new[] { new PageDimensions(1, 612, 792, "point") }; // 8.5in x 11in
        var di     = new[] { new PageDimensions(1, 8.5, 11.0, "inch") };

        var (warnings, infos) = PageDimensionWarningsHelper.GetPageDimensionWarnings(native, di, "doc.pdf");

        Assert.IsFalse(warnings.Any(w => w.Code == "PageDimensionMismatch"));
        Assert.AreEqual(0, infos.Count);
    }

    [TestMethod]
    public void PageDimensions_OutsideTolerance_ProducesMismatchWarning()
    {
        var native = new[] { new PageDimensions(1, 612, 792, "point") };        // 8.5in x 11in
        var di     = new[] { new PageDimensions(1, 8.0, 11.0, "inch") };        // 0.5in off on width

        var (warnings, _) = PageDimensionWarningsHelper.GetPageDimensionWarnings(native, di, "doc.pdf");

        Assert.IsTrue(warnings.Any(w => w.Code == "PageDimensionMismatch"));
    }

    [TestMethod]
    public void PageDimensions_NonInchUnit_ProducesInfoNotWarning()
    {
        var native = new[] { new PageDimensions(1, 612, 792, "point") };
        var di     = new[] { new PageDimensions(1, 100, 200, "pixel") };

        var (warnings, infos) = PageDimensionWarningsHelper.GetPageDimensionWarnings(native, di, "doc.pdf");

        Assert.IsFalse(warnings.Any(w => w.Code == "PageDimensionMismatch"));
        Assert.IsTrue(infos.Any(i => i.Code == "PageDimensionUnitUnsupported"));
    }

    [TestMethod]
    public void PageDimensions_NativeReadFailed_ReturnsEmpty()
    {
        var di = new[] { new PageDimensions(1, 8.0, 11.0, "inch") };

        var (warnings, infos) = PageDimensionWarningsHelper.GetPageDimensionWarnings(null, di, "doc.pdf");

        Assert.AreEqual(0, warnings.Count);
        Assert.AreEqual(0, infos.Count);
    }

    [TestMethod]
    public void PageDimensions_UnmatchedPageNumber_IsSkipped()
    {
        var native = new[] { new PageDimensions(1, 612, 792, "point") };
        var di     = new[] { new PageDimensions(2, 8.5, 11.0, "inch") }; // no page 2 on native side

        var (warnings, infos) = PageDimensionWarningsHelper.GetPageDimensionWarnings(native, di, "doc.pdf");

        Assert.AreEqual(0, warnings.Count);
        Assert.AreEqual(0, infos.Count);
    }

    private static DocMetadata NativeMetadata(int pageCount, IReadOnlyList<PageDimensions>? nativePageDimensions = null) => new(
        Title: null, Author: null, CreatedAt: null, ModDate: null,
        Producer: null, Creator: null, Subject: null, Keywords: null,
        PageCount: pageCount, Bookmarks: null,
        IsEncrypted: false, FormFields: null, EmbeddedFiles: null, Xmp: null,
        NativePageDimensions: nativePageDimensions);

    [TestMethod]
    public void FewerDiPagesThanNativePdf_FailsWithTruncatedPages()
    {
        var result   = SinglePageResult("Only page present.");
        var outcome  = new AnalyzeOutcome(true, result, null);

        var built = BuildAnalyzer().BuildResults(result, "doc.pdf", NativeMetadata(pageCount: 13), outcome);

        Assert.IsFalse(built.Ok);
        Assert.AreEqual(PdfOpenFailureReason.TruncatedPages, built.Error!.Reason);
    }

    [TestMethod]
    public void DiPageCountMatchesNativePdf_Succeeds()
    {
        var result  = SinglePageResult("Only page present.");
        var outcome = new AnalyzeOutcome(true, result, null);

        var built = BuildAnalyzer().BuildResults(result, "doc.pdf", NativeMetadata(pageCount: 1), outcome);

        Assert.IsTrue(built.Ok);
    }

    [TestMethod]
    public void BuildResults_PageDimensionMismatch_MergesIntoWarnings()
    {
        // DI reports 8.0in x 11.0in; native PdfPig page is 612x792 points = 8.5in x 11in -
        // 0.5in off on width, outside PageDimensionWarningsHelper's tolerance.
        var result  = SinglePageResult("Only page present.", width: 8.0, height: 11.0, unit: "inch");
        var outcome = new AnalyzeOutcome(true, result, null);
        var native  = NativeMetadata(pageCount: 1, nativePageDimensions: [new PageDimensions(1, 612, 792, "point")]);

        var built = BuildAnalyzer().BuildResults(result, "doc.pdf", native, outcome);

        Assert.IsTrue(built.Ok);
        Assert.IsTrue(built.Warnings.Any(w => w.Code == "PageDimensionMismatch"));
    }

    [TestMethod]
    [DataRow(0, 0.00, "0 page(s)")]
    [DataRow(1, 0.01, "1 page(s)")]
    [DataRow(10, 0.10, "10 page(s)")]
    public void CostInfo_EchoesGivenCostAndPageCount(int pageCount, double estimatedCost, string expectedPageCount)
    {
        var info = GetQualityWarningsHelper.CostInfo((decimal)estimatedCost, pageCount, "doc.pdf");

        Assert.AreEqual("EstimatedCost", info.Code);
        StringAssert.Contains(info.Message, $"${estimatedCost:F2}");
        StringAssert.Contains(info.Message, expectedPageCount);
    }

    // --- IsRetryablePollFailure ---------------------------------------------------------

    [TestMethod]
    [DataRow(429)] [DataRow(500)] [DataRow(502)] [DataRow(503)] [DataRow(504)]
    public void RequestFailedException_WithRetryableStatus_IsRetryable(int status)
    {
        Assert.IsTrue(DocumentAnalysisPoller.IsRetryablePollFailure(new RequestFailedException(status, "x")));
    }

    [TestMethod]
    [DataRow(400)] [DataRow(401)] [DataRow(404)]
    public void RequestFailedException_WithNonRetryableStatus_IsNotRetryable(int status)
    {
        Assert.IsFalse(DocumentAnalysisPoller.IsRetryablePollFailure(new RequestFailedException(status, "x")));
    }

    [TestMethod]
    public void HttpRequestException_IsRetryable()
    {
        Assert.IsTrue(DocumentAnalysisPoller.IsRetryablePollFailure(new System.Net.Http.HttpRequestException("network blip")));
    }

    [TestMethod]
    public void IOException_IsRetryable()
    {
        Assert.IsTrue(DocumentAnalysisPoller.IsRetryablePollFailure(new IOException("stream error")));
    }

    [TestMethod]
    public void OperationCanceledException_IsNeverRetryable()
    {
        Assert.IsFalse(DocumentAnalysisPoller.IsRetryablePollFailure(new OperationCanceledException()));
    }

    [TestMethod]
    public void UnrelatedException_IsNotRetryable()
    {
        Assert.IsFalse(DocumentAnalysisPoller.IsRetryablePollFailure(new InvalidOperationException("bug")));
    }

    // --- RetryAfter ----------------------------------------------------------------------
    // Full Retry-After header parsing (delta-seconds vs HTTP-date, floor at MinRetryAfter)
    // is exercised end-to-end via DocumentIntelligenceExtractorTests, which drives real
    // Azure.Core Response objects through the SDK's own retry pipeline - building one by
    // hand here would just re-implement that machinery. This covers the one case that's
    // simple and meaningful in isolation: a RequestFailedException with no raw response at
    // all (the common construction path, `new RequestFailedException(status, message)`).

    [TestMethod]
    public void NoRawResponse_ReturnsNull()
    {
        // A RequestFailedException built from just (status, message) - the common case for
        // a plain SDK-thrown error - carries no raw response at all.
        var ex = new RequestFailedException(429, "throttled");

        Assert.IsNull(DocumentAnalysisPoller.RetryAfter(ex));
    }

    // --- CountSurrogatePairs ---------------------------------------------------------------

    [TestMethod]
    public void EmptyContent_HasNoSurrogatePairs()
    {
        Assert.AreEqual(0, PdfDocumentIntelligenceAnalyzer.CountSurrogatePairs(""));
    }

    [TestMethod]
    public void PlainAsciiContent_HasNoSurrogatePairs()
    {
        Assert.AreEqual(0, PdfDocumentIntelligenceAnalyzer.CountSurrogatePairs("Just plain text, no emoji."));
    }

    [TestMethod]
    public void OrdinaryBmpDiacritics_AreNotCountedAsSurrogatePairs()
    {
        // Dutch diacritics (client, informatie) fit in one UTF-16 code unit - not a
        // surrogate pair, unlike emoji/astral-plane characters.
        Assert.AreEqual(0, PdfDocumentIntelligenceAnalyzer.CountSurrogatePairs("cliënt geïnformeerd"));
    }

    [TestMethod]
    public void SingleEmoji_CountsAsOneSurrogatePair()
    {
        var content = "Look: \U0001F600 done"; // U+1F600 GRINNING FACE, one surrogate pair
        Assert.AreEqual(1, PdfDocumentIntelligenceAnalyzer.CountSurrogatePairs(content));
    }

    [TestMethod]
    public void MultipleEmoji_CountsEachPairSeparately()
    {
        var content = "\U0001F600\U0001F601\U0001F602";
        Assert.AreEqual(3, PdfDocumentIntelligenceAnalyzer.CountSurrogatePairs(content));
    }

    [TestMethod]
    public void LoneHighSurrogateWithNoFollowingLowSurrogate_IsNotCounted()
    {
        var content = "x" + '\uD83D' + "y"; // unpaired high surrogate
        Assert.AreEqual(0, PdfDocumentIntelligenceAnalyzer.CountSurrogatePairs(content));
    }

    [TestMethod]
    public void LoneLowSurrogateWithNoPrecedingHighSurrogate_IsNotCounted()
    {
        var content = "x" + '\uDE00' + "y"; // unpaired low surrogate
        Assert.AreEqual(0, PdfDocumentIntelligenceAnalyzer.CountSurrogatePairs(content));
    }

    // --- ValidateAnalyzeResult ------------------------------------------------------------

    [TestMethod]
    public void NonMarkdownContentFormat_FailsWithUnexpectedContentFormatReason()
    {
        var json = """
        {
          "apiVersion": "2024-11-30", "modelId": "prebuilt-layout", "content": "hi",
          "contentFormat": "text",
          "pages": [ { "pageNumber": 1, "words": [], "lines": [], "selectionMarks": [], "spans": [] } ],
          "paragraphs": [], "tables": [], "figures": [], "sections": [], "warnings": []
        }
        """;
        var result = ModelReaderWriter.Read<AnalyzeResult>(BinaryData.FromString(json))!;

        var outcome = BuildAnalyzer().ValidateAnalyzeResult(result, "doc.pdf");

        Assert.IsFalse(outcome.Ok);
        Assert.AreEqual(PdfOpenFailureReason.UnexpectedContentFormat, outcome.Error!.Reason);
    }

    [TestMethod]
    public void ZeroPages_FailsWithEmptyDocumentReason()
    {
        var json = """
        {
          "apiVersion": "2024-11-30", "modelId": "prebuilt-layout", "content": "",
          "contentFormat": "markdown",
          "pages": [],
          "paragraphs": [], "tables": [], "figures": [], "sections": [], "warnings": []
        }
        """;
        var result = ModelReaderWriter.Read<AnalyzeResult>(BinaryData.FromString(json))!;

        var outcome = BuildAnalyzer().ValidateAnalyzeResult(result, "doc.pdf");

        Assert.IsFalse(outcome.Ok);
        Assert.AreEqual(PdfOpenFailureReason.EmptyDocument, outcome.Error!.Reason);
    }

    [TestMethod]
    public void MarkdownWithPages_Succeeds_NoNonBmpWarning()
    {
        var result = SinglePageResult("clean ascii text");

        var outcome = BuildAnalyzer().ValidateAnalyzeResult(result, "doc.pdf");

        Assert.IsTrue(outcome.Ok);
        Assert.IsFalse(outcome.Warnings.Any(w => w.Code == "NonBmpCharacters"));
    }

    [TestMethod]
    public void MarkdownWithNonBmpCharacters_Succeeds_WithNonBmpWarning()
    {
        var result = SinglePageResult("emoji here: \U0001F600");

        var outcome = BuildAnalyzer().ValidateAnalyzeResult(result, "doc.pdf");

        Assert.IsTrue(outcome.Ok);
        Assert.IsTrue(outcome.Warnings.Any(w => w.Code == "NonBmpCharacters"));
    }

    // --- GetTables (GetTablesHelper.GetTables, internal for direct testing) ----------

    [TestMethod]
    public void NoTablesInResult_ReturnsEmptyList()
    {
        var result = SinglePageResult("no tables here");

        var tables = GetTablesHelper.GetTables(result);

        Assert.AreEqual(0, tables.Count);
    }

    [TestMethod]
    public void TablesInResult_AreExtractedWithCellsAndDimensions()
    {
        var json = """
        {
          "apiVersion": "2024-11-30", "modelId": "prebuilt-layout", "content": "table content",
          "contentFormat": "markdown",
          "pages": [ { "pageNumber": 1, "words": [], "lines": [], "selectionMarks": [], "spans": [ { "offset": 0, "length": 13 } ] } ],
          "paragraphs": [],
          "tables": [
            {
              "rowCount": 2, "columnCount": 2,
              "cells": [
                { "kind": "columnHeader", "rowIndex": 0, "columnIndex": 0, "content": "Name", "spans": [] },
                { "kind": "columnHeader", "rowIndex": 0, "columnIndex": 1, "content": "Dose", "spans": [] },
                { "rowIndex": 1, "columnIndex": 0, "content": "Aspirin", "spans": [] },
                { "rowIndex": 1, "columnIndex": 1, "content": "100mg", "spans": [] }
              ],
              "spans": [ { "offset": 0, "length": 5 } ],
              "boundingRegions": [ { "pageNumber": 1, "polygon": [] }]
            }
          ],
          "figures": [], "sections": [], "warnings": []
        }
        """;
        var result = ModelReaderWriter.Read<AnalyzeResult>(BinaryData.FromString(json))!;

        var tables = GetTablesHelper.GetTables(result);

        Assert.AreEqual(1, tables.Count);
        Assert.AreEqual(2, tables[0].RowCount);
        Assert.AreEqual(2, tables[0].ColumnCount);
        Assert.AreEqual(4, tables[0].Cells.Count);
        Assert.AreEqual(1, tables[0].PageNumber);
        Assert.AreEqual(0, tables[0].Offset);
    }
}
