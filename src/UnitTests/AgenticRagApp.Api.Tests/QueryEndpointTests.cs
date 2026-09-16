using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Api.Endpoints;
using AgenticRagApp.Observability.Reports;
using AgenticRagApp.Querying.Models;
using AgenticRagApp.Querying.Services;

namespace RagApp.UnitTests.Api;

// Mirrors QueryingFunctionTests case for case: the two hosts serve the same endpoint over the
// same service, so they should fail and succeed the same way. The handler is a static method
// returning typed results, so it is called directly - no host, no HttpContext fakes. What this
// cannot cover is the framework's own half (malformed JSON -> 400, routing, ProblemDetails
// bodies); that is checked by running the app - see the project README.
[TestClass]
public class QueryEndpointTests
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
        Category:            null,
        LatencyMs:           123,
        InputTokens:         10,
        OutputTokens:        20,
        TotalTokens:         30,
        ContextTokens:       15,
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

    private static Task<Results<Ok<QueryResponse>, ValidationProblem, ProblemHttpResult>> Handle(
        QueryRequest? body, Mock<IRagQueryService>? ragService = null, Mock<IRunReportWriter>? reportWriter = null) =>
        QueryEndpoint.HandleAsync(
            body!, // the parameter is non-nullable for the OpenAPI schema; null here tests the direct-caller guard
            (ragService ?? MockRagService()).Object,
            (reportWriter ?? MockReportWriter()).Object,
            NullLoggerFactory.Instance,
            CancellationToken.None);

    [TestMethod]
    public async Task HandleAsync_NullBody_ReturnsValidationProblem()
    {
        var result = await Handle(body: null);

        Assert.IsInstanceOfType<ValidationProblem>(result.Result);
        var problem = (ValidationProblem)result.Result;
        Assert.IsTrue(problem.ProblemDetails.Errors.ContainsKey("question"));
    }

    [TestMethod]
    public async Task HandleAsync_MissingQuestion_ReturnsValidationProblem()
    {
        var result = await Handle(new QueryRequest(""));

        Assert.IsInstanceOfType<ValidationProblem>(result.Result);
    }

    [TestMethod]
    public async Task HandleAsync_WhitespaceQuestion_ReturnsValidationProblem()
    {
        var result = await Handle(new QueryRequest("   "));

        Assert.IsInstanceOfType<ValidationProblem>(result.Result);
    }

    [TestMethod]
    public async Task HandleAsync_ValidQuestion_ReturnsOkWithAnswer()
    {
        var result = await Handle(new QueryRequest("What is it?"), MockRagService(Result(answer: "42")));

        Assert.IsInstanceOfType<Ok<QueryResponse>>(result.Result);
        var ok = (Ok<QueryResponse>)result.Result;
        Assert.AreEqual("42", ok.Value!.Answer);
        Assert.AreEqual(123, ok.Value.Telemetry.LatencyMs);
    }

    [TestMethod]
    public async Task HandleAsync_ValidQuestion_PassesQuestionToRagService()
    {
        var ragService = MockRagService();

        await Handle(new QueryRequest("What is it?"), ragService);

        ragService.Verify(s => s.AskAsync("What is it?", It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task HandleAsync_CitationsIncludedInResponse()
    {
        var citation = new Citation("doc1", "Title", "QC1", "rel/path", Page: 2);

        var result = await Handle(new QueryRequest("q"), MockRagService(Result(citations: [citation])));

        Assert.IsInstanceOfType<Ok<QueryResponse>>(result.Result);
        var ok     = (Ok<QueryResponse>)result.Result;
        var source = ok.Value!.Sources.Single();
        Assert.AreEqual("doc1", source.DocumentId);
        Assert.AreEqual("QC1", source.QuickCode);
        Assert.AreEqual("[Title] - p.2", source.Label);
        Assert.IsNull(source.Url);
    }

    [TestMethod]
    public async Task HandleAsync_RagServiceThrows_ReturnsInternalServerErrorProblem()
    {
        var result = await Handle(new QueryRequest("q"), MockRagService(throws: new InvalidOperationException("boom")));

        Assert.IsInstanceOfType<ProblemHttpResult>(result.Result);
        var problem = (ProblemHttpResult)result.Result;
        Assert.AreEqual(StatusCodes.Status500InternalServerError, problem.StatusCode);
        // The exception text stays in the log, never in the response.
        Assert.IsFalse(problem.ProblemDetails.Title!.Contains("boom"));
    }

    [TestMethod]
    public async Task HandleAsync_RagServiceThrowsOperationCanceled_ExceptionPropagatesRatherThanBeingSwallowed()
    {
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            Handle(new QueryRequest("q"), MockRagService(throws: new OperationCanceledException())));
    }

    [TestMethod]
    public async Task HandleAsync_ReportWriterEnabled_WritesReportUnderQueriesPath()
    {
        var reportWriter = MockReportWriter(isEnabled: true);

        await Handle(new QueryRequest("q"), reportWriter: reportWriter);

        reportWriter.Verify(w => w.WriteReportAsync(
            It.Is<string>(path => path.StartsWith("queries/") && path.EndsWith(".json")),
            It.IsAny<QueryRunReport>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task HandleAsync_ReportWriterDisabled_NoReportWritten()
    {
        var reportWriter = MockReportWriter(isEnabled: false);

        await Handle(new QueryRequest("q"), reportWriter: reportWriter);

        reportWriter.Verify(w => w.WriteReportAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
