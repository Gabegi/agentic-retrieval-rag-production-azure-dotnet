using Azure;
using Azure.AI.DocumentIntelligence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using AgenticRagApp.Infrastructure.Clients.DocumentIntelligence;
using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Indexing.Pdf.Services;
using AgenticRagApp.Common.Models;

namespace RagApp.UnitTests.PdfExtraction;

// Covers PdfDocumentIntelligenceAnalyzer.SubmitAndPollAsync's poll phase (the free
// UpdateStatusAsync loop against an already-submitted, already-billed operation) -
// distinct from DocumentIntelligenceExtractorTests, which already covers the submit
// phase's retry/exhaustion via IDocumentAnalysisClient.SubmitAnalyzeAsync itself.
// Operation<AnalyzeResult> is mocked directly with Moq (a non-sealed SDK type with a
// protected parameterless constructor, same approach DocumentAnalysisClientTests already
// uses), rather than driving a real analyze call end to end.
[TestClass]
public class PdfDocumentIntelligenceAnalyzerPollingTests
{
    // Instant, no-op delay: retry backoff runs on real (multi-second) TimeSpan schedules
    // otherwise, and a test exercising retry exhaustion would actually wait through it.
    private static PdfDocumentIntelligenceAnalyzer BuildAnalyzer(IDocumentAnalysisClient diClient) =>
        new(diClient, NullLogger<PdfDocumentIntelligenceAnalyzer>.Instance, delay: (_, _) => Task.CompletedTask);

    private static DocMetadata NativeMetadata(int pageCount = 1) => new(
        Title: null, Author: null, CreatedAt: null, ModDate: null,
        Producer: null, Creator: null, Subject: null, Keywords: null,
        PageCount: pageCount, Bookmarks: null,
        IsEncrypted: false, FormFields: null, EmbeddedFiles: null, Xmp: null,
        NativePageDimensions: null);

    private static AnalyzeResult ValidSinglePageResult()
    {
        const string content = "clean page text";
        var json = $$"""
        {
          "apiVersion": "2024-11-30", "modelId": "prebuilt-layout", "content": "{{content}}",
          "contentFormat": "markdown",
          "pages": [
            { "pageNumber": 1, "words": [], "lines": [], "selectionMarks": [], "spans": [ { "offset": 0, "length": {{content.Length}} } ] }
          ],
          "paragraphs": [], "tables": [], "figures": [], "sections": [], "warnings": []
        }
        """;
        return System.ClientModel.Primitives.ModelReaderWriter.Read<AnalyzeResult>(BinaryData.FromString(json))!;
    }

    private static Mock<IDocumentAnalysisClient> BuildDiClient(Operation<AnalyzeResult> operation)
    {
        var diClient = new Mock<IDocumentAnalysisClient>();
        diClient.Setup(c => c.SubmitAnalyzeAsync(It.IsAny<AnalyzeDocumentOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(operation);
        return diClient;
    }

    [TestMethod]
    public async Task OperationAlreadyComplete_WithValue_Succeeds_NoPolling()
    {
        var operation = new Mock<Operation<AnalyzeResult>>();
        operation.SetupGet(o => o.HasCompleted).Returns(true);
        operation.SetupGet(o => o.HasValue).Returns(true);
        operation.SetupGet(o => o.Value).Returns(ValidSinglePageResult());

        var analyzer = BuildAnalyzer(BuildDiClient(operation.Object).Object);

        var result = await analyzer.AnalyzeDocumentAsync([1, 2, 3], "doc.pdf", NativeMetadata(), CancellationToken.None);

        Assert.IsTrue(result.Ok);
        operation.Verify(o => o.UpdateStatusAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task PollRetryableFailure_ThenRecovers_Succeeds()
    {
        var operation = new Mock<Operation<AnalyzeResult>>();
        var completedCalls = 0;
        operation.SetupGet(o => o.HasCompleted).Returns(() => completedCalls >= 2); // false, false, then true
        operation.SetupGet(o => o.HasValue).Returns(true);
        operation.SetupGet(o => o.Value).Returns(ValidSinglePageResult());

        var updateCalls = 0;
        operation.Setup(o => o.UpdateStatusAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                updateCalls++;
                completedCalls++;
                if (updateCalls == 1) throw new RequestFailedException(503, "transient");
                return new ValueTask<Response>(Mock.Of<Response>());
            });

        var analyzer = BuildAnalyzer(BuildDiClient(operation.Object).Object);

        var result = await analyzer.AnalyzeDocumentAsync([1, 2, 3], "doc.pdf", NativeMetadata(), CancellationToken.None);

        Assert.IsTrue(result.Ok);
        Assert.AreEqual(2, updateCalls); // 1 failure + 1 success
    }

    [TestMethod]
    public async Task PollFailuresExhaustRetryBudget_ReturnsDiServiceError()
    {
        var operation = new Mock<Operation<AnalyzeResult>>();
        operation.SetupGet(o => o.HasCompleted).Returns(false); // never completes on its own
        operation.Setup(o => o.UpdateStatusAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(503, "sustained outage"));

        var analyzer = BuildAnalyzer(BuildDiClient(operation.Object).Object);

        var result = await analyzer.AnalyzeDocumentAsync([1, 2, 3], "doc.pdf", NativeMetadata(), CancellationToken.None);

        Assert.IsFalse(result.Ok);
        Assert.AreEqual(PdfOpenFailureReason.DiServiceError, result.Error!.Reason);
        // 1 initial attempt + 4 retries (BackoffDelays.Length) = 5 total before giving up.
        operation.Verify(o => o.UpdateStatusAsync(It.IsAny<CancellationToken>()), Times.Exactly(5));
    }

    [TestMethod]
    public async Task PollThrottled429_ReturnsThrottledReason()
    {
        var operation = new Mock<Operation<AnalyzeResult>>();
        operation.SetupGet(o => o.HasCompleted).Returns(false);
        operation.Setup(o => o.UpdateStatusAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(429, "throttled"));

        var analyzer = BuildAnalyzer(BuildDiClient(operation.Object).Object);

        var result = await analyzer.AnalyzeDocumentAsync([1, 2, 3], "doc.pdf", NativeMetadata(), CancellationToken.None);

        Assert.IsFalse(result.Ok);
        Assert.AreEqual(PdfOpenFailureReason.Throttled, result.Error!.Reason);
    }

    [TestMethod]
    public async Task PollThrowsUnexpectedException_ReturnsUnknownReason()
    {
        var operation = new Mock<Operation<AnalyzeResult>>();
        operation.SetupGet(o => o.HasCompleted).Returns(false);
        operation.Setup(o => o.UpdateStatusAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("bug, not a service failure"));

        var analyzer = BuildAnalyzer(BuildDiClient(operation.Object).Object);

        var result = await analyzer.AnalyzeDocumentAsync([1, 2, 3], "doc.pdf", NativeMetadata(), CancellationToken.None);

        Assert.IsFalse(result.Ok);
        Assert.AreEqual(PdfOpenFailureReason.Unknown, result.Error!.Reason);
        // An unexpected (non-retryable) exception must not be retried at all.
        operation.Verify(o => o.UpdateStatusAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task OperationCompletesWithNoValue_ReturnsMissingAnalysisResultReason()
    {
        var operation = new Mock<Operation<AnalyzeResult>>();
        operation.SetupGet(o => o.HasCompleted).Returns(true);
        operation.SetupGet(o => o.HasValue).Returns(false);

        var analyzer = BuildAnalyzer(BuildDiClient(operation.Object).Object);

        var result = await analyzer.AnalyzeDocumentAsync([1, 2, 3], "doc.pdf", NativeMetadata(), CancellationToken.None);

        Assert.IsFalse(result.Ok);
        Assert.AreEqual(PdfOpenFailureReason.MissingAnalysisResult, result.Error!.Reason);
    }

    [TestMethod]
    public async Task BudgetCeilingFires_ReturnsDiServiceErrorWithTimeoutMessage_NotACallerCancellation()
    {
        // Simulates SubmitAndPollAsync's own AnalyzeBudget ceiling firing (an internally
        // linked CancellationTokenSource.CancelAfter) rather than the caller's own token -
        // the outer catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        // branch is only reachable when the caller's ct is still live.
        var operation = new Mock<Operation<AnalyzeResult>>();
        operation.SetupGet(o => o.HasCompleted).Returns(false);
        operation.Setup(o => o.UpdateStatusAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var analyzer = BuildAnalyzer(BuildDiClient(operation.Object).Object);

        var result = await analyzer.AnalyzeDocumentAsync([1, 2, 3], "doc.pdf", NativeMetadata(), CancellationToken.None);

        Assert.IsFalse(result.Ok);
        Assert.AreEqual(PdfOpenFailureReason.DiServiceError, result.Error!.Reason);
        StringAssert.Contains(result.Error!.Message, "timed out");
    }
}
