using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using AgenticRagApp.Observability;
using AgenticRagApp.Observability.Reports;
using AgenticRagApp.Querying.Models;
using AgenticRagApp.Querying.Services;

namespace AgenticRagApp;

public class QueryingFunction
{
    private readonly IRagQueryService          _ragService;
    private readonly IRunReportWriter          _reportWriter;
    private readonly ILogger<QueryingFunction> _logger;

    public QueryingFunction(IRagQueryService ragService, IRunReportWriter reportWriter, ILogger<QueryingFunction> logger)
    {
        _ragService   = ragService;
        _reportWriter = reportWriter;
        _logger       = logger;
    }

    // POST /api/query   body: { "question": "..." }
    //
    // The App Service host (AgenticRagApp.Api, QueryEndpoint) serves the same route over the same
    // service; the response shape (QueryResponse) and the report mapping (QueryRunReportFactory)
    // live in AgenticRagApp.Querying so the two hosts cannot drift apart. Only the HTTP plumbing
    // is this file's own.
    [Function("Query")]
    public async Task<HttpResponseData> RunQuery(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "query")] HttpRequestData req,
        FunctionContext context)
    {
        QueryRequest? body;
        try
        {
            body = await req.ReadFromJsonAsync<QueryRequest>();
        }
        // ReadFromJsonAsync surfaces malformed-JSON failures as an AggregateException
        // wrapping the JsonException rather than a bare JsonException, so a plain
        // `catch (JsonException)` never fires. GetBaseException() unwraps single-inner
        // exception chains (which is what a Task-continuation fault produces here) down
        // to the root cause.
        catch (Exception ex) when (ex.GetBaseException() is JsonException)
        {
            _logger.LogWarning(ex, "Query request body was not valid JSON");
            var malformed = req.CreateResponse(HttpStatusCode.BadRequest);
            await malformed.WriteStringAsync("Request body must be valid JSON matching { \"question\": string }");
            return malformed;
        }

        if (string.IsNullOrWhiteSpace(body?.Question))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("'question' is required");
            return bad;
        }

        try
        {
            var timestamp = DateTimeOffset.UtcNow;
            var result    = await _ragService.AskAsync(body.Question, context.CancellationToken);

            Instrumentation.QueryFinishReason.Add(1,
                new KeyValuePair<string, object?>("reason", result.FinishReason),
                new KeyValuePair<string, object?>("category", result.Category ?? "none"));

            _logger.LogInformation(
                "Query telemetry: {LatencyMs}ms, in={In} tokens, out={Out} tokens, reason={FinishReason}, category={Category}",
                result.LatencyMs, result.InputTokens, result.OutputTokens, result.FinishReason, result.Category);

            if (_reportWriter.IsEnabled)
                await _reportWriter.WriteReportAsync(
                    QueryRunReportFactory.BlobPath(timestamp),
                    QueryRunReportFactory.Create(body.Question, timestamp, result),
                    context.CancellationToken);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(QueryResponse.From(result));
            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Query failed");
            var error = req.CreateResponse(HttpStatusCode.InternalServerError);
            await error.WriteStringAsync("An error occurred while processing the query.");
            return error;
        }
    }

    private sealed record QueryRequest(string Question);
}
