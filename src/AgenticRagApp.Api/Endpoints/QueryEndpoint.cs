using Microsoft.AspNetCore.Http.HttpResults;
using AgenticRagApp.Observability;
using AgenticRagApp.Observability.Reports;
using AgenticRagApp.Querying.Models;
using AgenticRagApp.Querying.Services;

namespace AgenticRagApp.Api.Endpoints;

// Request body of POST /api/query - the same { "question": "..." } the Functions host takes.
// Question is nullable because a body of {} binds it to null and the handler, not the type
// system, answers that with the 400 below; an absent or malformed body never reaches the
// handler (the framework's own 400, problem+json via AddProblemDetails).
public sealed record QueryRequest(string? Question);

// The query endpoint: the App Service counterpart of QueryingFunction, over the same
// IRagQueryService, writing the same QueryRunReport, returning the same QueryResponse. Only the
// HTTP plumbing differs (minimal API + ProblemDetails here, HttpRequestData there).
public static class QueryEndpoint
{
    public static IEndpointRouteBuilder MapQueryEndpoints(this IEndpointRouteBuilder app)
    {
        // Same route and body as the Functions host, so a client moves between the two by
        // changing the base URL.
        //
        // POST, not GET, although the call reads and changes nothing in the index: the question
        // is free text that can carry personal data (acceptance criterion 5 exists because it
        // will), and a GET puts it in the URL - App Service, front-door and proxy access logs,
        // browser history, the OutSystems side's own logging. A POST body stays out of all of
        // those. GET also promises "safe, cacheable, repeatable", and a query is none of that: it
        // costs a knowledge-base retrieval plus answer synthesis and writes a per-query report,
        // so nothing (a CDN, a prefetching client, a retrying proxy) should repeat it on its own.
        app.MapPost("/api/query", HandleAsync)
           .WithName("Query")
           .WithSummary("Ask the knowledge base a question")
           .WithDescription(
               "Runs the question through the Azure AI Search knowledge base (agentic retrieval + " +
               "answer synthesis) and the query-time guards, and returns the answer with its " +
               "cited sources. Dutch in, Dutch out.");

        return app;
    }

    // body is non-nullable on purpose: a nullable parameter makes the OpenAPI request schema a
    // oneOf [null, QueryRequest], which is exactly the construct a specification importer is
    // least likely to handle; non-nullable gives a plain $ref, and the framework rejects an
    // empty body with a 400 before this runs. The null-check below still guards direct callers.
    public static async Task<Results<Ok<QueryResponse>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        QueryRequest      body,
        IRagQueryService  ragService,
        IRunReportWriter  reportWriter,
        ILoggerFactory    loggerFactory,
        CancellationToken ct)
    {
        // Static class, so no ILogger<QueryEndpoint> - the category is still this type's name.
        var logger = loggerFactory.CreateLogger(typeof(QueryEndpoint));

        if (string.IsNullOrWhiteSpace(body?.Question)) // body itself can only be null for a direct caller
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["question"] = ["'question' is required"],
            });

        try
        {
            var timestamp = DateTimeOffset.UtcNow;
            var result    = await ragService.AskAsync(body.Question, ct);

            Instrumentation.QueryFinishReason.Add(1,
                new KeyValuePair<string, object?>("reason", result.FinishReason),
                new KeyValuePair<string, object?>("category", result.Category ?? "none"));

            logger.LogInformation(
                "Query telemetry: {LatencyMs}ms, in={In} tokens, out={Out} tokens, reason={FinishReason}, category={Category}",
                result.LatencyMs, result.InputTokens, result.OutputTokens, result.FinishReason, result.Category);

            if (reportWriter.IsEnabled)
                await reportWriter.WriteReportAsync(
                    QueryRunReportFactory.BlobPath(timestamp),
                    QueryRunReportFactory.Create(body.Question, timestamp, result),
                    ct);

            return TypedResults.Ok(QueryResponse.From(result));
        }
        // A cancelled request has no client left to answer; let it propagate like the Functions
        // host does. Everything else becomes a 500 with a fixed message - the exception is in
        // the log, not in the response.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Query failed");
            return TypedResults.Problem(
                title:      "An error occurred while processing the query.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}
