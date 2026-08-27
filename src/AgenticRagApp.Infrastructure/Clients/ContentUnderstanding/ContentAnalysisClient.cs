using System.Text.Json;
using Azure;
using Azure.AI.ContentUnderstanding;
using Microsoft.Extensions.Logging;

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
    // does not create: the completion and text-embedding-3-large deployments themselves
    // (infra/ai_deployments.tf; the completion model is gpt-5.4-mini since 2026-08-27, named in
    // ContentUnderstandingDefaultsSetup rather than here).
    private const string AnalyzerId = "prebuilt-documentSearch";

    private readonly ContentUnderstandingClient      _client;
    private readonly ILogger<ContentAnalysisClient>? _logger;

    // How the SDK usage read last failed, logged ONCE per host lifetime (this client is a
    // singleton) rather than once per document - 51 identical warnings per run is noise, and
    // the question this answers ("why does GetUsage yield nothing?") has one answer per build.
    // 0 = not yet logged. Observability plan 1.1: TryGetUsage used to swallow this silently,
    // which is why the null was undiagnosable for two days of runs.
    private int _usageReadFailureLogged;

    public ContentAnalysisClient(ContentUnderstandingClient client, ILogger<ContentAnalysisClient>? logger = null)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<ContentAnalysis> AnalyzeAsync(byte[] bytes, CancellationToken ct = default)
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
            var rawJson = TryGetRawJson(operation);
            return new ContentAnalysis(operation.Value, TryGetUsage(operation, rawJson), rawJson);
        }
        catch (RequestFailedException ex)
        {
            var detail = OperationErrorDetail(operation);
            if (detail is null) throw;

            throw new RequestFailedException(
                ex.Status, $"{ex.Message}\nOperation error: {detail}", ex.ErrorCode, ex);
        }
    }

    // Best-effort: usage is reporting data, and a response the extension cannot read must not
    // fail an analysis that succeeded.
    //
    // SDK-first with a raw fallback (plan 1.2, decided 2026-08-26): GetUsage() is documented to
    // return null - not throw - when "usage data is not present in the response", yet run
    // 8b137234's raw capture shows the final poll DOES carry a usage node at the operation
    // envelope. So when the SDK yields nothing, the same envelope is parsed from the raw body
    // this client already holds for diagnostics. Whichever produced it, the reason the SDK
    // path yielded nothing is logged once per host (see _usageReadFailureLogged) - the silent
    // catch this replaces is why the null went undiagnosed across every run before 2026-08-26.
    private CuUsage? TryGetUsage(Operation<AnalysisResult> operation, string? rawJson)
    {
        Exception? sdkFailure = null;
        try
        {
            if (operation.GetUsage() is { } details)
                return CuUsage.From(details);
        }
        catch (Exception ex)
        {
            sdkFailure = ex;
        }

        var fallback = TryParseUsageFromRaw(rawJson);

        if (Interlocked.Exchange(ref _usageReadFailureLogged, 1) == 0)
            _logger?.LogWarning(sdkFailure,
                "SDK GetUsage() yielded no usage ({Reason}); raw-response fallback {FallbackOutcome}. Logged once per host - subsequent documents this run take the same path silently.",
                sdkFailure is null ? "returned null" : sdkFailure.GetType().Name,
                fallback is null ? "also found none" : "recovered it");

        return fallback;
    }

    // The fallback producer: the operation envelope's "usage" node, a documented sibling of
    // "result" (shape verified against run 8b137234's cu-raw-response capture). Best-effort for
    // the same reason as everything else here - null over an exception, always.
    private static CuUsage? TryParseUsageFromRaw(string? rawJson)
    {
        if (rawJson is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (!doc.RootElement.TryGetProperty("usage", out var usage)) return null;

            int? pages   = usage.TryGetProperty("documentPagesStandard", out var p) && p.TryGetInt32(out var pv) ? pv : null;
            int? minimal = usage.TryGetProperty("documentPagesMinimal", out var m) && m.TryGetInt32(out var mv) ? mv : null;
            int? basic   = usage.TryGetProperty("documentPagesBasic", out var b) && b.TryGetInt32(out var bv) ? bv : null;
            int? ctx     = usage.TryGetProperty("contextualizationTokens", out var c) && c.TryGetInt32(out var cv) ? cv : null;

            var tokens = new Dictionary<string, int>();
            if (usage.TryGetProperty("tokens", out var map) && map.ValueKind == JsonValueKind.Object)
                foreach (var entry in map.EnumerateObject())
                    if (entry.Value.TryGetInt32(out var count))
                        tokens[entry.Name] = count;

            // A usage node with nothing readable in it is "no usage", not an all-zero bill.
            return pages is null && minimal is null && basic is null && ctx is null && tokens.Count == 0
                ? null
                : new CuUsage(pages, ctx, tokens) { DocumentPagesMinimal = minimal, DocumentPagesBasic = basic };
        }
        catch { return null; }
    }

    // Best-effort for the same reason: the raw body is diagnostics (the cu-raw-response
    // capture), and a response that cannot be read back must not fail an analysis that
    // succeeded.
    private static string? TryGetRawJson(Operation<AnalysisResult> operation)
    {
        try
        {
            var content = operation.GetRawResponse()?.Content;
            return content is null || content.ToMemory().IsEmpty ? null : content.ToString();
        }
        catch { return null; }
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
