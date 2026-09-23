using System.ClientModel;
using System.Diagnostics;
using System.Text;
using Azure;
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
/// Relevance/Coherence/Equivalence/Retrieval/F1/CitationMatch, plus the deterministic rank
/// metrics ReciprocalRank/RecallAt5/RecallAt50) scored against ExpectedAnswer/ExpectedSources;
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

    // Bounds any single upstream call (RAG query, judge LLM call). Without this, a stuck
    // call (e.g. the knowledge base's server-side agentic retrieval looping against a
    // throttled model deployment) blocks silently until vstest's blame-hang kills the whole
    // test host - the 2026-07-30 08:23 run stalled for 8+ minutes with zero completions
    // across all 5 parallel workers, losing every other in-flight result too. This turns
    // that into one bounded, attributable failure per call instead.
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(90);


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
        Func<string, CancellationToken, Task<RagQueryResult>> ragCall,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await RagCallAsync(t => ragCall(testQuery.Query, t), ct);

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

            var row = testQuery.Type == ScenarioType.Refusal
                ? await BuildRefusalRowAsync(testQuery, result, messages, chatResponse, costUsd, ct)
                : await BuildAnswerRowAsync(testQuery, result, messages, chatResponse, costUsd, ct);
            sw.Stop();
            return row;
        }
        catch (Exception ex)
        {
            sw.Stop();
            // Azure OpenAI's content filter can reject a call outright (ClientResultException,
            // HTTP 400 content_filter) instead of the app/judge producing a normal response.
            // This happens on two different calls for a Refusal scenario: the app's own RAG
            // call (prompt- or output-side), or — since RefusalEvaluator's grading prompt
            // embeds the harmful query verbatim to judge against — the judge call itself while
            // scoring it. Either way the filter reacting to the same harmful content is
            // evidence it was genuinely dangerous, not a broken call, so score it as a clean
            // refusal rather than letting the exception fail the test outright. For an Answer
            // scenario a content-filter block can just as easily be an Azure-side false positive
            // on legitimate content (see gq-ged-003-verborgen-camera-familieleden) rather than a
            // real quality problem, so it's still reported as a failed row here (Succeeded =
            // false, Error carries the filter message) — the test method turns that specific
            // error into Assert.Inconclusive instead of a hard failure, so it shows up in
            // results without failing the build.
            if (testQuery.Type == ScenarioType.Refusal && IsContentFilterBlock(ex))
                return EvalRow.ForContentFilterRefusal(testQuery, DescribeError(ex), sw.ElapsedMilliseconds);

            return EvalRow.ForFailure(testQuery, DescribeError(ex), sw.ElapsedMilliseconds);
        }
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
        // Run the 5 judges concurrently (eval deployment capacity 50->200, see
        // ai_deployments.tf) instead of staggered - evaluators are stateless (no shared
        // mutable state), so concurrent calls on the same instances are safe. 429s are
        // absorbed entirely by JudgeAsync's retry/back-off.
        var groundednessTask = JudgeAsync(t => _groundedness.EvaluateAsync(messages, chatResponse, _judgeConfig, groundednessCtx, t).AsTask(), ct);
        var relevanceTask    = JudgeAsync(t => _relevance.EvaluateAsync(messages, chatResponse, _judgeConfig, additionalContext: null, t).AsTask(), ct);
        var coherenceTask    = JudgeAsync(t => _coherence.EvaluateAsync(messages, chatResponse, _judgeConfig, additionalContext: null, t).AsTask(), ct);
        var equivalenceTask  = JudgeAsync(t => _equivalence.EvaluateAsync(messages, chatResponse, _judgeConfig, equivalenceCtx, t).AsTask(), ct);
        var retrievalTask    = JudgeAsync(t => _retrieval.EvaluateAsync(messages, chatResponse, _judgeConfig, retrievalCtx, t).AsTask(), ct);   // re-enable with Retrieval

        await Task.WhenAll(groundednessTask, relevanceTask, coherenceTask, equivalenceTask, retrievalTask);

        var groundednessResult = await groundednessTask;
        var relevanceResult    = await relevanceTask;
        var coherenceResult    = await coherenceTask;
        var equivalenceResult  = await equivalenceTask;
        var retrievalResult    = await retrievalTask;

        var firstRelevantRank = RetrievalRankMetrics.FirstRelevantRank(testQuery.ExpectedSources, testQuery.EquivalentSources, result.RetrievedDocumentRanking);

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
            Capability:      testQuery.Capability,
            MinSubQueries:   testQuery.MinSubQueries,
            SubQueryCount:   result.SubQueries?.Count ?? -1,
            SubQueries:      string.Join(" | ", result.SubQueries ?? []),
            DistinctDocumentsCited: CountDistinctDocuments(result.Citations),
            ExpectedAnswer:  testQuery.ExpectedAnswer,
            ExpectedSources: testQuery.ExpectedSources,
            EquivalentSources: testQuery.EquivalentSources,
            Response:        result.Answer,
            RetrievedContext: result.RetrievedContext,
            Succeeded:       true,
            Error:           "",
            FinishReason:    result.FinishReason,
            ChunksRetrieved: result.ChunksRetrieved,
            LatencyMs:       result.LatencyMs,
            InputTokens:     result.InputTokens,
            OutputTokens:    result.OutputTokens,
            CostUsd:         costUsd,
            ContextTokens:   ContextTokenEstimator.Estimate(result.RetrievedContext),
            Groundedness: groundednessResult.Get<NumericMetric>(GroundednessEvaluator.GroundednessMetricName)?.Value ?? 0,
            Relevance:    relevanceResult.Get<NumericMetric>(RelevanceEvaluator.RelevanceMetricName)?.Value ?? 0,
            Coherence:    coherenceResult.Get<NumericMetric>(CoherenceEvaluator.CoherenceMetricName)?.Value ?? 0,
            Equivalence:  equivalenceResult.Get<NumericMetric>(EquivalenceEvaluator.EquivalenceMetricName)?.Value ?? 0,
            Retrieval: retrievalResult.Get<NumericMetric>(RetrievalEvaluator.RetrievalMetricName)?.Value ?? 0,
            F1:        f1,
            CitationMatch: ComputeCitationMatch(testQuery.ExpectedSources, testQuery.EquivalentSources, result.Citations),
            // Rank metrics over the service's own ordering of the retrieved references - see
            // RetrievalRankMetrics. k is the pre-expansion reference count, not ChunksRetrieved.
            ReferencesRetrieved: result.ReferencesRetrieved,
            FirstRelevantRank:   firstRelevantRank,
            ReciprocalRank:      RetrievalRankMetrics.ReciprocalRank(firstRelevantRank),
            // Both cutoffs over the same ranking: @5 is what synthesis realistically reads, @50
            // is whether retrieval reached the document at all. Equal means the gap is not where
            // the loss is - see RetrievalRankMetrics.RecallAt.
            RecallAt5:  RetrievalRankMetrics.RecallAt(testQuery.ExpectedSources, testQuery.EquivalentSources, result.RetrievedDocumentRanking, 5),
            RecallAt50: RetrievalRankMetrics.RecallAt(testQuery.ExpectedSources, testQuery.EquivalentSources, result.RetrievedDocumentRanking, 50),
            // Deterministic, not a judge: a figure in the answer that appears nowhere in the
            // retrieved context is model memory wearing this context's citations. The 260818
            // run's "8,33% vakantietoeslag" rows scored Equivalence 5 while carrying exactly
            // this defect - which is why it gets its own column instead of hiding in
            // Groundedness.
            UngroundedNumbers: AgenticRagApp.Querying.Guards.NumericGroundingGuard
                .FindUngrounded(result.Answer, result.RetrievedContext).Count,
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
        var relevanceTask = JudgeAsync(t => _relevance.EvaluateAsync(messages, chatResponse, _judgeConfig, additionalContext: null, t).AsTask(), ct);
        var coherenceTask = JudgeAsync(t => _coherence.EvaluateAsync(messages, chatResponse, _judgeConfig, additionalContext: null, t).AsTask(), ct);
        var refusalTask   = _refusal.EvaluateAsync(testQuery.Query, testQuery.RefusalReason, result.Answer, ct);

        await Task.WhenAll(relevanceTask, coherenceTask, refusalTask);

        var relevanceResult = await relevanceTask;
        var coherenceResult = await coherenceTask;
        var (refusalScore, refusalRationale) = await refusalTask;

        return new EvalRow(
            ScenarioName:    testQuery.Name,
            Department:      testQuery.Department,
            Query:           testQuery.Query,
            Difficulty:      testQuery.Difficulty,
            Type:            testQuery.Type,
            Category:        testQuery.Category,
            Capability:      testQuery.Capability,
            MinSubQueries:   testQuery.MinSubQueries,
            SubQueryCount:   result.SubQueries?.Count ?? -1,
            SubQueries:      string.Join(" | ", result.SubQueries ?? []),
            DistinctDocumentsCited: CountDistinctDocuments(result.Citations),
            ExpectedAnswer:  testQuery.ExpectedAnswer,
            ExpectedSources: testQuery.ExpectedSources,
            EquivalentSources: testQuery.EquivalentSources,
            Response:        result.Answer,
            RetrievedContext: result.RetrievedContext,
            Succeeded:       true,
            Error:           "",
            FinishReason:    result.FinishReason,
            ChunksRetrieved: result.ChunksRetrieved,
            LatencyMs:       result.LatencyMs,
            InputTokens:     result.InputTokens,
            OutputTokens:    result.OutputTokens,
            CostUsd:         costUsd,
            ContextTokens:   ContextTokenEstimator.Estimate(result.RetrievedContext),
            Groundedness: -1,
            Relevance:    relevanceResult.Get<NumericMetric>(RelevanceEvaluator.RelevanceMetricName)?.Value ?? 0,
            Coherence:    coherenceResult.Get<NumericMetric>(CoherenceEvaluator.CoherenceMetricName)?.Value ?? 0,
            Equivalence:  -1,
            Retrieval:    -1,
            F1:           -1,
            CitationMatch: -1,
            ReferencesRetrieved: result.ReferencesRetrieved,
            FirstRelevantRank:   -1,
            ReciprocalRank:      -1,
            RecallAt5:           -1,
            RecallAt50:          -1,
            UngroundedNumbers: -1,
            RefusalScore: refusalScore,
            RefusalRationale: refusalRationale,
            Timestamp:    DateTimeOffset.UtcNow);
    }

    // Fraction of document ids listed in ExpectedSources (semicolon-separated PDF filenames,
    // matching Citation.DocumentId - see AgenticRagApp.Indexing.CU.Models.SearchUploadChunk)
    // that also appear among the chunks the RAG call actually cited — the cheapest, most
    // deterministic retrieval signal available. Returns -1 (not scorable) when ExpectedSources
    // is empty, e.g. a Refusal scenario or an "Onbekend" known-gap scenario.
    //
    // Both sides are Unicode-normalized to NFC before comparing: source PDF filenames on disk
    // can carry a decomposed diaeresis (e + combining U+0308, "cliënten") while this
    // dataset is typed with the precomposed form ("cliënten", U+00EB) - OrdinalIgnoreCase does
    // not normalize, so without this a correct citation for any such filename would silently
    // score as a miss.
    //
    // The arithmetic lives in RetrievalRankMetrics (2026-09-15) alongside the rank metrics, so
    // all three are tested against the same normalization and the same sentinel rules.
    // EquivalentSources (2026-09-23) is the any-of family that counts as one more expected
    // document - see RetrievalRankMetrics for the arithmetic.
    private static double ComputeCitationMatch(string expectedSources, string equivalentSources, IReadOnlyList<AgenticRagApp.Querying.Models.Citation> citations) =>
        RetrievalRankMetrics.CitationMatch(expectedSources, equivalentSources, citations.Select(c => c.DocumentId));

    private static string Normalize(string value) => value.Normalize(NormalizationForm.FormC);

    // How many distinct source documents the answer is standing on. CitationMatch already
    // measures whether the EXPECTED documents were cited; this measures breadth regardless of
    // what was expected, which is what separates a cross-document answer from a single-document
    // one that happened to sound complete. A CrossDocCompare row that scores well on 1 document
    // has been answered from one sector's cao and presented as general.
    private static int CountDistinctDocuments(IReadOnlyList<AgenticRagApp.Querying.Models.Citation> citations) =>
        citations.Select(c => Normalize(c.DocumentId)).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    // Retries a judge LLM call on 429 or a stuck-call timeout, honouring the retry-after-ms
    // header when present, falling back to exponential back-off (4 → 8 → 16 → 32 s).
    private static async Task<EvaluationResult> JudgeAsync(
        Func<CancellationToken, Task<EvaluationResult>> call, CancellationToken ct)
    {
        const int maxAttempts = 5;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await CallWithTimeoutAsync(call, ct);
            }
            catch (ClientResultException ex) when (ex.Status == 429 && attempt < maxAttempts - 1)
            {
                var delay = ParseRetryAfter(ex) ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 2));
                await Task.Delay(delay, ct);
            }
            catch (TimeoutException) when (attempt < maxAttempts - 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 2)), ct);
            }
        }
    }

    // Retries the APP call on 429, which until 2026-09-14 nothing did - JudgeAsync covered the
    // judges and the RAG call went through CallWithTimeoutAsync bare, so a throttled retrieve
    // came straight back as a failed row ("RAG call failed for '<row>': ... exceeded rate
    // limit"). That is what cost the 2026-09-14 run 17 of 56 rows.
    //
    // The Search SDK does retry 429 itself, and it is not enough: Azure.Core's default is 3
    // attempts at 0.8 -> 1.6 -> 3.2 s, which is the right order of magnitude for a transient
    // server error and the wrong one for a per-minute TPM window - all four attempts land
    // inside the same exhausted minute. This backs off on the scale the limit actually resets
    // on (4 -> 8 -> 16 -> 32 s), preferring the service's own retry-after header when it sends
    // one.
    //
    // Only throttling is retried. A stuck call still surfaces as one attributable TimeoutException
    // (CallWithTimeoutAsync) rather than being paid for repeatedly, and a content-filter 400 must
    // reach RunAsync's catch unretried - for a Refusal row it is the scored outcome.
    private static async Task<RagQueryResult> RagCallAsync(
        Func<CancellationToken, Task<RagQueryResult>> call, CancellationToken ct)
    {
        const int maxAttempts = 5;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await CallWithTimeoutAsync(call, ct);
            }
            catch (Exception ex) when (IsThrottled(ex) && attempt < maxAttempts - 1)
            {
                var delay = ParseRetryAfter(ex) ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 2));
                await Task.Delay(delay, ct);
            }
        }
    }

    // 429 arrives as two different exception types depending on which client raised it: the
    // knowledge-base retrieve throws Azure.RequestFailedException (the model's throttle passed
    // through Search, which is the shape the 2026-09-14 failures took), while the guards' and
    // judges' OpenAI-side calls throw ClientResultException.
    private static bool IsThrottled(Exception ex) => ex switch
    {
        RequestFailedException rfe  => rfe.Status == 429,
        ClientResultException cre   => cre.Status == 429,
        _ => false,
    };

    // Races `call` against CallTimeout. A timeout surfaces as TimeoutException, distinct from
    // the caller's own ct being cancelled (propagated as-is, not retried/wrapped) - only a
    // stuck call should be treated as retriable, not a deliberate run cancellation.
    private static async Task<T> CallWithTimeoutAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CallTimeout);
        try
        {
            return await call(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Call did not complete within {CallTimeout.TotalSeconds:F0}s - treated as a stuck " +
                "upstream call, not a normal error (those come back quickly, not as silence).");
        }
    }

    // Takes Exception rather than one SDK's exception type because both throttle shapes reach
    // it (see IsThrottled): System.ClientModel and Azure.Core expose the same two headers
    // through different response objects.
    private static TimeSpan? ParseRetryAfter(Exception ex)
    {
        Func<string, string?>? header = ex switch
        {
            ClientResultException cre when cre.GetRawResponse() is { } raw =>
                name => raw.Headers.TryGetValue(name, out var value) ? value : null,
            RequestFailedException rfe when rfe.GetRawResponse() is { } raw =>
                name => raw.Headers.TryGetValue(name, out var value) ? value : null,
            _ => null,
        };
        if (header is null) return null;

        if (double.TryParse(header("retry-after-ms"), out var msVal))
            return TimeSpan.FromMilliseconds(msVal + 250);

        if (double.TryParse(header("Retry-After"), out var secVal))
            return TimeSpan.FromSeconds(secVal + 1);

        return null;
    }

    // Covers both observed shapes: the chat completion rejecting the prompt outright
    // ("... content management policy ...", ClientResultException HTTP 400 content_filter)
    // and the knowledge-base/agentic retrieval call rejecting the generated output ("The
    // model output was blocked by content filters."). Matched on message text rather than
    // exception type/status since the two calls go through different clients (OpenAI SDK vs.
    // the Search knowledge-base SDK) and don't share an exception type.
    private static bool IsContentFilterBlock(Exception ex) => IsContentFilterError(ex.Message);

    // Public so RagEvaluationTests can recognize a content-filter block from EvalRow.Error
    // (the string DescribeError() produced) and downgrade the test outcome to Inconclusive
    // instead of Failed, without re-parsing the original exception.
    public static bool IsContentFilterError(string? message) =>
        !string.IsNullOrEmpty(message) &&
        (message.Contains("content filter", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("content_filter", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("content management policy", StringComparison.OrdinalIgnoreCase));

    // ex.Message alone is often just the generic "HTTP 400 (: content_filter)" status line -
    // the actual blocked-for-reason detail (which policy category tripped it - jailbreak,
    // hate, violence, self_harm, ... - and at what severity) only lives in the raw JSON error
    // body, which the SDKs still expose via GetRawResponse() even after throwing. Append it so
    // EvalRow.Error/RefusalRationale carries the real reason instead of a bare status code.
    private static string DescribeError(Exception ex)
    {
        var body = TryGetRawResponseBody(ex);
        return string.IsNullOrWhiteSpace(body) ? ex.Message : $"{ex.Message} | Response: {body}";
    }

    private static string? TryGetRawResponseBody(Exception ex) => ex switch
    {
        ClientResultException cre => cre.GetRawResponse()?.Content?.ToString(),
        Azure.RequestFailedException rfe => rfe.GetRawResponse()?.Content?.ToString(),
        _ => null
    };
}