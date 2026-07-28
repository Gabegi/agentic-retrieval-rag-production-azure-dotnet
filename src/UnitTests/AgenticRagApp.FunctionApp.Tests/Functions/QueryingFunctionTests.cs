using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp;
using AgenticRagApp.Observability.Reports;
using AgenticRagApp.Querying.Models;
using AgenticRagApp.Querying.Services;

namespace RagApp.UnitTests.Functions;

[TestClass]
public class QueryingFunctionTests
{
    private static RagQueryResult Result(string answer = "The answer", IReadOnlyList<Citation>? citations = null) => new(
        Answer:              answer,
        RetrievedContext:    "context",
        SystemInstructions:  "instructions",
        ChunksRetrieved:     3,
        OperationName:       "chat",
        ProviderName:        "azure_openai",
        ServerAddress:       "openai.example.com",
        ServerPort:          443,
        ConversationId:      "conv-1",
        Model:               "gpt-model",
        FinishReason:        "stop",
        LatencyMs:           123,
        InputTokens:         10,
        OutputTokens:        20,
        TotalTokens:         30,
        Temperature:         null,
        MaxOutputTokens:     null,
        TopP:                null,
        TopK:                null,
        FrequencyPenalty:    null,
        PresencePenalty:     null,
        Seed:                null,
        ResponseFormat:      null,
        StopSequences:       null,
        Citations:           citations ?? []);

    private static Mock<IRagQueryService> MockRagService(RagQueryResult? result = null, Exception? throws = null)
    {
        var mock = new Mock<IRagQueryService>();
        if (throws is not null)
            mock.Setup(s => s.AskAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(throws);
        else
            mock.Setup(s => s.AskAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(result ?? Result());
        return mock;
    }

    private static Mock<IRunReportWriter> MockReportWriter(bool isEnabled = true)
    {
        var mock = new Mock<IRunReportWriter>();
        mock.SetupGet(w => w.IsEnabled).Returns(isEnabled);
        mock.Setup(w => w.WriteReportAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return mock;
    }

    private static QueryingFunction BuildFunction(Mock<IRagQueryService> ragService, Mock<IRunReportWriter>? reportWriter = null) =>
        new(ragService.Object, (reportWriter ?? MockReportWriter()).Object, NullLogger<QueryingFunction>.Instance);

    [TestMethod]
    public async Task RunQuery_MalformedJsonBody_ReturnsBadRequest()
    {
        // HttpRequestDataExtensions.ReadFromJsonAsync<T> surfaces malformed-JSON failures
        // as an AggregateException wrapping the JsonException (a Task-continuation fault),
        // not a bare JsonException, so RunQuery's catch filters on GetBaseException() to
        // unwrap that chain down to the root JsonException.
        var function = BuildFunction(MockRagService());
        var context  = new FakeFunctionContext();
        var request  = new FakeHttpRequestData(context, "not json at all");

        var response = (FakeHttpResponseData)await function.RunQuery(request, context);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task RunQuery_MissingQuestion_ReturnsBadRequest()
    {
        var function = BuildFunction(MockRagService());
        var context  = new FakeFunctionContext();
        var request  = new FakeHttpRequestData(context, JsonSerializer.Serialize(new { question = "" }));

        var response = (FakeHttpResponseData)await function.RunQuery(request, context);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task RunQuery_WhitespaceQuestion_ReturnsBadRequest()
    {
        var function = BuildFunction(MockRagService());
        var context  = new FakeFunctionContext();
        var request  = new FakeHttpRequestData(context, JsonSerializer.Serialize(new { question = "   " }));

        var response = (FakeHttpResponseData)await function.RunQuery(request, context);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task RunQuery_ValidQuestion_ReturnsOkWithAnswer()
    {
        var ragService = MockRagService(Result(answer: "42"));
        var function   = BuildFunction(ragService);
        var context    = new FakeFunctionContext();
        var request    = new FakeHttpRequestData(context, JsonSerializer.Serialize(new { question = "What is it?" }));

        var response = (FakeHttpResponseData)await function.RunQuery(request, context);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var json = response.ReadBodyAsString();
        StringAssert.Contains(json, "42");
    }

    [TestMethod]
    public async Task RunQuery_ValidQuestion_PassesQuestionToRagService()
    {
        var ragService = MockRagService();
        var function   = BuildFunction(ragService);
        var context    = new FakeFunctionContext();
        var request    = new FakeHttpRequestData(context, JsonSerializer.Serialize(new { question = "What is it?" }));

        await function.RunQuery(request, context);

        ragService.Verify(s => s.AskAsync("What is it?", It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task RunQuery_CitationsIncludedInResponseBody()
    {
        var citation   = new Citation("doc1", "Title", "QC1", "rel/path");
        var ragService = MockRagService(Result(citations: [citation]));
        var function   = BuildFunction(ragService);
        var context    = new FakeFunctionContext();
        var request    = new FakeHttpRequestData(context, JsonSerializer.Serialize(new { question = "q" }));

        var response = (FakeHttpResponseData)await function.RunQuery(request, context);

        var json = response.ReadBodyAsString();
        StringAssert.Contains(json, "doc1");
        StringAssert.Contains(json, "QC1");
    }

    [TestMethod]
    public async Task RunQuery_RagServiceThrows_ReturnsInternalServerError()
    {
        var ragService = MockRagService(throws: new InvalidOperationException("boom"));
        var function   = BuildFunction(ragService);
        var context    = new FakeFunctionContext();
        var request    = new FakeHttpRequestData(context, JsonSerializer.Serialize(new { question = "q" }));

        var response = (FakeHttpResponseData)await function.RunQuery(request, context);

        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [TestMethod]
    public async Task RunQuery_RagServiceThrowsOperationCanceled_ExceptionPropagatesRatherThanBeingSwallowed()
    {
        var ragService = MockRagService(throws: new OperationCanceledException());
        var function   = BuildFunction(ragService);
        var context    = new FakeFunctionContext();
        var request    = new FakeHttpRequestData(context, JsonSerializer.Serialize(new { question = "q" }));

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => function.RunQuery(request, context));
    }

    [TestMethod]
    public async Task RunQuery_ReportWriterEnabled_WritesReport()
    {
        var reportWriter = MockReportWriter(isEnabled: true);
        var function     = BuildFunction(MockRagService(), reportWriter);
        var context      = new FakeFunctionContext();
        var request      = new FakeHttpRequestData(context, JsonSerializer.Serialize(new { question = "q" }));

        await function.RunQuery(request, context);

        reportWriter.Verify(w => w.WriteReportAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task RunQuery_ReportWriterDisabled_NoReportWritten()
    {
        var reportWriter = MockReportWriter(isEnabled: false);
        var function     = BuildFunction(MockRagService(), reportWriter);
        var context      = new FakeFunctionContext();
        var request      = new FakeHttpRequestData(context, JsonSerializer.Serialize(new { question = "q" }));

        await function.RunQuery(request, context);

        reportWriter.Verify(w => w.WriteReportAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
