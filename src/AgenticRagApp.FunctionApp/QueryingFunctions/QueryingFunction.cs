using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using AgenticRagApp.Observability;
using AgenticRagApp.Observability.Reports;
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
                    $"queries/{timestamp:yyyy/MM/dd}/{timestamp:HH-mm-ss}.json",
                    new QueryRunReport(
                        RunId:              result.ConversationId,
                        Timestamp:          timestamp,
                        Question:           body.Question,
                        Answer:             result.Answer,
                        RetrievedContext:   result.RetrievedContext,
                        SystemInstructions: result.SystemInstructions,
                        ChunksRetrieved:    result.ChunksRetrieved,
                        OperationName:      result.OperationName,
                        ProviderName:       result.ProviderName,
                        ServerAddress:      result.ServerAddress,
                        ServerPort:         result.ServerPort,
                        ConversationId:     result.ConversationId,
                        Model:              result.Model,
                        FinishReason:       result.FinishReason,
                        Category:           result.Category,
                        LatencyMs:          result.LatencyMs,
                        InputTokens:        result.InputTokens,
                        OutputTokens:       result.OutputTokens,
                        TotalTokens:        result.TotalTokens,
                        ContextTokens:      result.ContextTokens,
                        Temperature:        result.Temperature,
                        MaxOutputTokens:    result.MaxOutputTokens,
                        TopP:               result.TopP,
                        TopK:               result.TopK,
                        FrequencyPenalty:   result.FrequencyPenalty,
                        PresencePenalty:    result.PresencePenalty,
                        Seed:               result.Seed,
                        ResponseFormat:     result.ResponseFormat,
                        StopSequences:      result.StopSequences),
                    context.CancellationToken);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                answer    = result.Answer,
                category  = result.Category,
                // Criterion 7: "[Title] - p.(page number)" — the acceptance criterion's own
                // example (`[Vilans protocollen voor neustampon] - p.2`) has no parentheses
                // around the page number despite its prose header reading "p.(page number)";
                // built to match the example. Both the pre-formatted label and the raw
                // title/page fields are sent, since no frontend exists in this repo to confirm
                // which one is expected to do the formatting - see
                // docs/2608/260806/remaining-acceptance-criteria-plan.md, item 4.
                sources   = result.Citations.Select(c => new
                {
                    document_id   = c.DocumentId,
                    title         = c.Title,
                    quick_code    = c.QuickCode,
                    relative_path = c.RelativePath,
                    page          = c.Page,
                    page_count    = c.PageCount,
                    created_at    = c.CreatedAt,
                    mod_date      = c.ModDate,
                    label         = c.Title is not null && c.Page is not null
                        ? $"[{c.Title}] - p.{c.Page}"
                        : c.Title,
                    url           = c.ZenyaUrl,
                }),
                telemetry = new
                {
                    latency_ms    = result.LatencyMs,
                    input_tokens  = result.InputTokens,
                    output_tokens = result.OutputTokens
                }
            });
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
