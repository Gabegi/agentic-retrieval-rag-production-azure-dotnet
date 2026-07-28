using System.ClientModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.NLP;
using Microsoft.Extensions.AI.Evaluation.Quality;
using AgenticRagApp.Querying.Models;
using AgenticRagApp.Querying.Services;
using RagApp.Evaluation.Tests.Models;

namespace RagApp.Evaluation.Tests.Evaluation;

/// <summary>
/// Calls the RAG app for a given TestQuery, scores the response (evaluators run
/// sequentially), and returns the result as an EvalRow. Does no I/O beyond the ragCall
/// itself — persistence is EvalResultWriter's job.
///
/// Branches on TestQuery.Type: Answer scenarios get the full metric suite (Groundedness/
/// Relevance/Coherence/Equivalence/Retrieval/F1/CitationMatch) scored against ExpectedAnswer;
/// Refusal scenarios (prompt injection, medical/legal advice, privacy, ...) have no "correct
/// answer" to score against, so only Relevance/Coherence plus RefusalEvaluator's did-it-
/// actually-decline judgment apply — the rest are left at -1.
///
/// We deliberately evaluate the OUTCOME (final answer) only. The agentic evaluators
/// (IntentResolution, TaskAdherence, ToolCallAccuracy) are skipped: they need the
/// agent's internal tool-call trace, which our single-turn test data does not carry.
/// </summary>
public sealed class RagEvaluator
{
    // GPT-4.1 list pricing (USD per 1 M tokens) — update when model changes.
    private const double InputUsdPerMToken  = 2.00;
    private const double OutputUsdPerMToken = 8.00;

    // ExpectedSources carries free-text notes ("SharePoint: ...") alongside Zenya document
    // URLs — the document GUID embedded in those URLs is the only part that reliably lines
    // up with Citation.DocumentId, so that's what gets matched against.
    private static readonly Regex DocumentIdPattern = new(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled);

    private readonly GroundednessEvaluator _groundedness = new();
    private readonly RelevanceEvaluator   _relevance    = new();
    private readonly CoherenceEvaluator   _coherence    = new();
    private readonly EquivalenceEvaluator _equivalence  = new();
    private readonly RetrievalEvaluator   _retrieval    = new();  // re-enable with Retrieval
    private readonly F1Evaluator          _f1           = new();  // re-enable with F1
    private readonly RefusalEvaluator     _refusal;
    private readonly ChatConfiguration    _judgeConfig;

    public RagEvaluator(IChatClient judgeClient)
    {
        _judgeConfig = new ChatConfiguration(judgeClient);
        _refusal = new RefusalEvaluator(judgeClient);
    }

    public async Task<EvalRow> RunAsync(
        TestQuery testQuery,
        Func<string, Task<RagQueryResult>> ragCall,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        RagQueryResult result;
        try
        {
            result = await ragCall(testQuery.Query);
            sw.Stop();
        }
        catch (Exception ex)
        {
            sw.Stop();
            return EvalRow.ForFailure(testQuery, ex.Message, sw.ElapsedMilliseconds);
        }

        var costUsd = (result.InputTokens * InputUsdPerMToken + result.OutputTokens * OutputUsdPerMToken) / 1_000_000.0;

        var chatResponse = new ChatResponse([new ChatMessage(ChatRole.Assistant, result.Answer)])
        {
            Usage = new UsageDetails
            {
                InputTokenCount = result.InputTokens,
                OutputTokenCount = result.OutputTokens,
                TotalTokenCount = result.InputTokens + result.OutputTokens
            }
        };

        var messages = new List<ChatMessage> { new(ChatRole.User, testQuery.Query) };

        return testQuery.Type == ScenarioType.Refusal
            ? await BuildRefusalRowAsync(testQuery, result, messages, chatResponse, costUsd, ct)
            : await BuildAnswerRowAsync(testQuery, result, messages, chatResponse, costUsd, ct);
    }

    // Answer scenarios: judged against ExpectedAnswer with the full metric suite. Refusal
    // fields are left at -1 (not applicable — there's no "correct answer" text to refuse).
    private async Task<EvalRow> BuildAnswerRowAsync(
        TestQuery testQuery, RagQueryResult result, List<ChatMessage> messages, ChatResponse chatResponse,
        double costUsd, CancellationToken ct)
    {
        var groundednessCtx = new List<EvaluationContext>
        {
            new GroundednessEvaluatorContext(result.RetrievedContext)
        };
        var equivalenceCtx = new List<EvaluationContext>
        {
            new EquivalenceEvaluatorContext(testQuery.ExpectedAnswer)
        };
        var retrievalCtx = new List<EvaluationContext>   // re-enable with Retrieval
        {
            new RetrievalEvaluatorContext(result.RetrievedContext)
        };
        // Run evaluators sequentially with 2 s gaps; retry handles residual 429s via Retry-After headers.
        var groundednessResult = await JudgeAsync(() => _groundedness.EvaluateAsync(messages, chatResponse, _judgeConfig, groundednessCtx, ct).AsTask(), ct);
        await Task.Delay(2000, ct);
        var relevanceResult    = await JudgeAsync(() => _relevance.EvaluateAsync(messages, chatResponse, _judgeConfig, additionalContext: null, ct).AsTask(), ct);
        await Task.Delay(2000, ct);
        var coherenceResult    = await JudgeAsync(() => _coherence.EvaluateAsync(messages, chatResponse, _judgeConfig, additionalContext: null, ct).AsTask(), ct);
        await Task.Delay(2000, ct);
        var equivalenceResult  = await JudgeAsync(() => _equivalence.EvaluateAsync(messages, chatResponse, _judgeConfig, equivalenceCtx, ct).AsTask(), ct);
        await Task.Delay(2000, ct);                                                                                                                           // re-enable with Retrieval
        var retrievalResult = await JudgeAsync(() => _retrieval.EvaluateAsync(messages, chatResponse, _judgeConfig, retrievalCtx, ct).AsTask(), ct);          // re-enable with Retrieval

        // F1 (token overlap) is only meaningful when the corpus can produce the reference
        // answer. Known-gap scenarios get -1 so dashboards can exclude them from trends.
        double f1 = -1;
        if (testQuery.AnswerableFromCorpus)
        {
            var f1Ctx = new List<EvaluationContext> { new F1EvaluatorContext(testQuery.ExpectedAnswer) };
            var f1Result = await _f1.EvaluateAsync(messages, chatResponse, null, f1Ctx, ct);
            f1 = f1Result.Get<NumericMetric>(F1Evaluator.F1MetricName)?.Value ?? 0;
        }

        return new EvalRow(
            ScenarioName:    testQuery.Name,
            Department:      testQuery.Department,
            Query:           testQuery.Query,
            Difficulty:      testQuery.Difficulty,
            Type:            testQuery.Type,
            Category:        testQuery.Category,
            ExpectedAnswer:  testQuery.ExpectedAnswer,
            ExpectedSources: testQuery.ExpectedSources,
            Response:        result.Answer,
            RetrievedContext: result.RetrievedContext,
            Succeeded:       true,
            Error:           "",
            LatencyMs:       result.LatencyMs,
            InputTokens:     result.InputTokens,
            OutputTokens:    result.OutputTokens,
            CostUsd:         costUsd,
            Groundedness: groundednessResult.Get<NumericMetric>(GroundednessEvaluator.GroundednessMetricName)?.Value ?? 0,
            Relevance:    relevanceResult.Get<NumericMetric>(RelevanceEvaluator.RelevanceMetricName)?.Value ?? 0,
            Coherence:    coherenceResult.Get<NumericMetric>(CoherenceEvaluator.CoherenceMetricName)?.Value ?? 0,
            Equivalence:  equivalenceResult.Get<NumericMetric>(EquivalenceEvaluator.EquivalenceMetricName)?.Value ?? 0,
            Retrieval: retrievalResult.Get<NumericMetric>(RetrievalEvaluator.RetrievalMetricName)?.Value ?? 0,
            F1:        f1,
            CitationMatch: ComputeCitationMatch(testQuery.ExpectedSources, result.Citations),
            RefusalScore:    -1,
            RefusalRationale: "",
            Timestamp:    DateTimeOffset.UtcNow);
    }

    // Refusal scenarios: there is no "correct answer" text to score Groundedness/Equivalence/
    // Retrieval/F1/CitationMatch against, so those are left at -1. Relevance/Coherence still
    // apply (the refusal itself should be a relevant, well-formed reply), and RefusalScore is
    // the actual pass/fail signal — did the assistant decline without complying or leaking?
    private async Task<EvalRow> BuildRefusalRowAsync(
        TestQuery testQuery, RagQueryResult result, List<ChatMessage> messages, ChatResponse chatResponse,
        double costUsd, CancellationToken ct)
    {
        var relevanceResult = await JudgeAsync(() => _relevance.EvaluateAsync(messages, chatResponse, _judgeConfig, additionalContext: null, ct).AsTask(), ct);
        await Task.Delay(2000, ct);
        var coherenceResult = await JudgeAsync(() => _coherence.EvaluateAsync(messages, chatResponse, _judgeConfig, additionalContext: null, ct).AsTask(), ct);
        await Task.Delay(2000, ct);
        var (refusalScore, refusalRationale) = await _refusal.EvaluateAsync(
            testQuery.Query, testQuery.RefusalReason, result.Answer, ct);

        return new EvalRow(
            ScenarioName:    testQuery.Name,
            Department:      testQuery.Department,
            Query:           testQuery.Query,
            Difficulty:      testQuery.Difficulty,
            Type:            testQuery.Type,
            Category:        testQuery.Category,
            ExpectedAnswer:  testQuery.ExpectedAnswer,
            ExpectedSources: testQuery.ExpectedSources,
            Response:        result.Answer,
            RetrievedContext: result.RetrievedContext,
            Succeeded:       true,
            Error:           "",
            LatencyMs:       result.LatencyMs,
            InputTokens:     result.InputTokens,
            OutputTokens:    result.OutputTokens,
            CostUsd:         costUsd,
            Groundedness: -1,
            Relevance:    relevanceResult.Get<NumericMetric>(RelevanceEvaluator.RelevanceMetricName)?.Value ?? 0,
            Coherence:    coherenceResult.Get<NumericMetric>(CoherenceEvaluator.CoherenceMetricName)?.Value ?? 0,
            Equivalence:  -1,
            Retrieval:    -1,
            F1:           -1,
            CitationMatch: -1,
            RefusalScore: refusalScore,
            RefusalRationale: refusalRationale,
            Timestamp:    DateTimeOffset.UtcNow);
    }

    // Fraction of document IDs found in ExpectedSources that also appear in the chunks the
    // RAG call actually cited — the cheapest, most deterministic retrieval signal available.
    // Returns -1 (not scorable) when ExpectedSources carries no document ID at all, e.g. a
    // free-text SharePoint note or an "Onbekend" known-gap scenario.
    private static double ComputeCitationMatch(string expectedSources, IReadOnlyList<AgenticRagApp.Querying.Models.Citation> citations)
    {
        var expectedIds = DocumentIdPattern.Matches(expectedSources)
            .Select(m => m.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (expectedIds.Count == 0) return -1;

        var citedIds = citations.Select(c => c.DocumentId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matched  = expectedIds.Count(id => citedIds.Contains(id));
        return matched / (double)expectedIds.Count;
    }

    // Retries a judge LLM call on 429, honouring the retry-after-ms header when present,
    // falling back to exponential back-off (4 → 8 → 16 → 32 s).
    private static async Task<EvaluationResult> JudgeAsync(
        Func<Task<EvaluationResult>> call, CancellationToken ct)
    {
        const int maxAttempts = 5;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await call();
            }
            catch (ClientResultException ex) when (ex.Status == 429 && attempt < maxAttempts - 1)
            {
                var delay = ParseRetryAfter(ex) ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 2));
                await Task.Delay(delay, ct);
            }
        }
    }

    private static TimeSpan? ParseRetryAfter(ClientResultException ex)
    {
        var raw = ex.GetRawResponse();
        if (raw is null) return null;

        if (raw.Headers.TryGetValue("retry-after-ms", out var ms) && double.TryParse(ms, out var msVal))
            return TimeSpan.FromMilliseconds(msVal + 250);

        if (raw.Headers.TryGetValue("Retry-After", out var sec) && double.TryParse(sec, out var secVal))
            return TimeSpan.FromSeconds(secVal + 1);

        return null;
    }
}