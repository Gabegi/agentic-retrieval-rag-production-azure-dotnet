using System.Text.Json;
using Azure;
using Azure.AI.ContentUnderstanding;

namespace AgenticRagApp.Infrastructure.Clients.ContentUnderstanding;

// Submit a PDF to Content Understanding and wait for the result. That is the whole client.
//
// Shaped after the documented sample for prebuilt analyzers, which drives them through
// Analyze(inputs) - new AnalysisInput { Uri = ... } there, inline Data here - rather than the
// binary route (AnalyzeBinaryAsync, tried first, failed every call with a sanitized
// InvalidRequest on 2026-08-25).
//
// The models the analyzer resolves against come from the account-wide default
// model->deployment mapping, which ContentUnderstandingDefaultsSetup verifies (and fixes if
// wrong) once at host startup - see that class for the ownership reasoning. This client
// deliberately names no models itself.
public sealed class ContentAnalysisClient : IContentAnalysisClient
{
    // The prebuilt analyzer, not a custom one. One prerequisite on the account, which this app
    // does not create: the gpt-4.1-mini and text-embedding-3-large deployments themselves
    // (infra/ai_deployments.tf).
    private const string AnalyzerId = "prebuilt-documentSearch";

    private readonly ContentUnderstandingClient _client;

    public ContentAnalysisClient(ContentUnderstandingClient client) => _client = client;

    public async Task<AnalysisResult> AnalyzeAsync(byte[] bytes, CancellationToken ct = default)
    {
        var input = new AnalysisInput
        {
            Data     = BinaryData.FromBytes(bytes),
            MimeType = "application/pdf",
        };

        // Geography, not the service default (Global): this corpus is a Dutch care
        // organisation's documents, and Global means "may be processed in any Azure data center".
        //
        // Started rather than Completed so the Operation survives a failure. An analysis that
        // completes as Failed surfaces as a RequestFailedException whose message Azure.Core
        // sanitizes down to the bare error code ("Invalid request. Status: 200") - 2xx response
        // bodies are never included in exception messages. The body of the final poll, which
        // carries the service's actual error, is still on the operation; both first live runs
        // (2026-08-25) were undiagnosable without it.
        Operation<AnalysisResult> operation = await _client.AnalyzeAsync(
            WaitUntil.Started,
            AnalyzerId,
            inputs: new[] { input },
            processingLocation: ProcessingLocation.Geography,
            cancellationToken: ct);

        try
        {
            await operation.WaitForCompletionAsync(ct);
            return operation.Value;
        }
        catch (RequestFailedException ex)
        {
            var detail = OperationErrorDetail(operation);
            if (detail is null) throw;

            throw new RequestFailedException(
                ex.Status, $"{ex.Message}\nOperation error: {detail}", ex.ErrorCode, ex);
        }
    }

    // The failed operation's own "error" object, read off the last polled response. Capped
    // because the message ends up inside ExtractionStageMetrics.Issues, which travels through
    // Durable Table Storage under its 64KB row limit. Null when there is nothing usable to add.
    private static string? OperationErrorDetail(Operation<AnalysisResult> operation)
    {
        try
        {
            var content = operation.GetRawResponse()?.Content;
            if (content is null || content.ToMemory().IsEmpty) return null;

            using var doc = JsonDocument.Parse(content);
            if (!doc.RootElement.TryGetProperty("error", out var error)) return null;

            var text = error.GetRawText();
            return text.Length <= 500 ? text : text[..500];
        }
        catch
        {
            // Diagnostics only - a response that is unreadable or not JSON must not replace the
            // original exception.
            return null;
        }
    }
}
