using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgenticRagApp.Observability.Reports;

// The evaluative section of the email: reads the assembled run summary and produces findings
// plus ranked improvement suggestions.
//
// NOT to be confused with the answer-quality eval harness (RagApp.Evaluation.Tests), which
// scores responses against golden questions and is a separate test run. This reads the
// pipeline's own reports and says what to change.
//
// Reuses the IChatClient already registered against config.OpenAiGptDeployment - no new AI
// resource. Sees the summary only, never raw artifacts.
public sealed class RunAnalysisAgent
{
    private const int MaxOutputTokens = 1600;

    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = false };

    private readonly IChatClient _chat;
    private readonly ILogger<RunAnalysisAgent> _logger;

    public RunAnalysisAgent(IChatClient chat, ILogger<RunAnalysisAgent> logger)
    {
        _chat   = chat;
        _logger = logger;
    }

    public async Task<RunAssessment?> AnalyseAsync(RunEmailSummary summary, CancellationToken ct)
    {
        try
        {
            var payload = JsonSerializer.Serialize(ToModelInput(summary), s_json);

            var response = await _chat.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, SystemPrompt),
                    new ChatMessage(ChatRole.User, payload),
                ],
                new ChatOptions
                {
                    MaxOutputTokens = MaxOutputTokens,
                    // Low, not zero: this is analysis, and the output is checked against the
                    // deterministic numbers printed beside it either way.
                    Temperature    = 0.2f,
                    ResponseFormat = ChatResponseFormat.Json,
                },
                ct);

            return Parse(response.Text);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The deterministic report is the thing that matters. A model failure degrades this
            // one section to "assessment unavailable" and the email still sends.
            _logger.LogWarning(ex, "Run analysis failed for instance {InstanceId} — sending without the assessment section",
                summary.InstanceId);
            return null;
        }
    }

    private const string SystemPrompt = """
        You analyse one run of a document ingestion pipeline that feeds a RAG system over Dutch
        HR/compliance policy PDFs. You are given that run's metrics, quality flags, and a small
        sample of the chunks it produced.

        Produce JSON with exactly this shape:
        {
          "narrative": "2-4 sentences on what changed and why it matters",
          "suggestions": [
            { "suggestion": "...", "evidence": "...", "expectedImpact": "...", "effort": "config|code|content-owner" }
          ],
          "whatIsFine": "one line naming the metrics that look healthy"
        }

        Rules:
        - EVERY suggestion must cite the specific metric name(s) or sample(s) it came from in
          "evidence". A suggestion you cannot ground in the supplied data must be omitted
          entirely. An ungrounded suggestion is worse than none: it gets acted on once and
          trusted thereafter.
        - Rank suggestions by expected retrieval impact, best first. Give at most 5, and give
          none at all if the run looks healthy.
        - Be concrete and specific to this pipeline: chunk size or strategy, split boundaries,
          document metadata, cleaning transforms, extraction settings, embedding config.
          "Improve chunking" is not a suggestion; "lower the table-boundary split threshold" is.
        - A null stage means it never ran. Never describe a null stage as having produced zero.
        - If there is no previous run to compare against, say so rather than inventing a trend.
        - Do not assign severities or an overall verdict - those are computed deterministically
          elsewhere. Explain; do not decide.
        - Name what is healthy in "whatIsFine". Silence on healthy metrics reads as an oversight
          and undermines the flagged ones.
        """;

    // Deliberately a projection, not the whole summary: excludes the raw sibling-report blobs
    // and caps the samples. The model needs the shape of the run, not every field of it.
    private static object ToModelInput(RunEmailSummary s)
    {
        var r = s.IndexReport;

        return new
        {
            kind    = s.Kind.ToString(),
            success = s.Success,
            error   = r?.Run.ErrorMessage,
            forceReindex = r?.Run.ForceReindex,
            durationSeconds = r is null ? (double?)null : (r.Run.FinishedAt - r.Run.StartedAt).TotalSeconds,

            corpusDocumentCount = s.CorpusDocumentCount,

            // Null stages stay null on the wire - the prompt depends on the model being able to
            // tell "never ran" from "measured zero".
            extraction = r?.Extraction,
            chunking   = r?.Chunking,
            embedding  = r?.Embedding,
            restore    = s.RestoreReport,

            validation = s.Validation,
            fileFacts  = s.FileFacts,
            diff       = s.Diff,
            failure    = s.Failure,

            previousRun  = s.Previous,
            evalBaseline = s.EvalBaseline,

            flags = s.Flags.Select(f => new
            {
                severity = f.Severity.ToString(),
                f.Metric, f.Observed, f.Expected, f.Meaning,
            }),
        };
    }

    private RunAssessment? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        try
        {
            using var doc = JsonDocument.Parse(ExtractJson(text));
            var root = doc.RootElement;

            var suggestions = new List<ImprovementSuggestion>();
            if (root.TryGetProperty("suggestions", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var suggestion = Str(item, "suggestion");
                    var evidence   = Str(item, "evidence");

                    // Enforced here, not just asked for in the prompt: a suggestion without
                    // evidence is dropped rather than rendered unsourced.
                    if (string.IsNullOrWhiteSpace(suggestion) || string.IsNullOrWhiteSpace(evidence))
                    {
                        _logger.LogInformation("Dropped an ungrounded suggestion from the run assessment");
                        continue;
                    }

                    suggestions.Add(new ImprovementSuggestion(
                        suggestion, evidence, Str(item, "expectedImpact") ?? "", Str(item, "effort") ?? ""));
                }
            }

            return new RunAssessment(
                Narrative:   Str(root, "narrative") ?? "",
                Suggestions: suggestions,
                WhatIsFine:  Str(root, "whatIsFine") ?? "");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Run assessment was not valid JSON — sending without the assessment section");
            return null;
        }
    }

    // Models occasionally wrap JSON in a fenced block despite ResponseFormat.Json.
    private static string ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end   = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
