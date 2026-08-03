namespace RagApp.Evaluation.Tests.Models;

public record EvalRow(
    // Identity
    string          ScenarioName,
    string          Department,        // Afdeling — for filtering/reporting
    string          Query,              // Vraag
    string          Difficulty,         // Lastigheid
    ScenarioType    Type,               // Answer or Refusal — which metrics below are actually scored
    string          Category,           // protocol / buiten_scope / medisch_advies / promptinjectie / ...

    // Golden truth (what we expected)
    string          ExpectedAnswer,     // Antwoord
    string          ExpectedSources,    // Bronnen

    // Actual output
    string          Response,
    string          RetrievedContext,
    bool            Succeeded,
    string          Error,

    // Performance
    long            LatencyMs,
    long            InputTokens,
    long            OutputTokens,
    double          CostUsd,            // (InputTokens × inputPrice + OutputTokens × outputPrice) / 1M

    // Scores — Answer scenarios only (−1 = not scored, e.g. a Refusal scenario)
    double          Groundedness,       // 1-5  LLM — response grounded in retrieved context?
    double          Relevance,          // 1-5  LLM — response relevant to the question?
    double          Coherence,          // 1-5  LLM — response coherent and well-formed?
    double          Equivalence,        // 1-5  LLM — same meaning as expected answer?
    double       Retrieval,          // 1-5  LLM — was the right context fetched?  (re-enable with Retrieval)
    double       F1,                 // 0-1  NLP — token overlap vs expected answer (re-enable with F1)
    double       CitationMatch,      // 0-1  deterministic — fraction of ExpectedSources doc IDs present in Citations; -1 if ExpectedSources has no matchable doc ID

    // Scores — Refusal scenarios only (−1 = not scored, e.g. an Answer scenario)
    double          RefusalScore,       // 1-5  LLM — did the response appropriately decline, without complying or leaking? (see RefusalEvaluator)
    string          RefusalRationale,   // one-sentence judge explanation for RefusalScore

    DateTimeOffset Timestamp)
{
    /// <summary>Builds a row representing a failed RAG call, with all scores zeroed.</summary>
    public static EvalRow ForFailure(TestQuery q, string error, long latencyMs) => new(
        ScenarioName: q.Name,
        Department: q.Department,
        Query: q.Query,
        Difficulty: q.Difficulty,
        Type: q.Type,
        Category: q.Category,
        ExpectedAnswer: q.ExpectedAnswer,
        ExpectedSources: q.ExpectedSources,
        Response: "",
        RetrievedContext: "",
        Succeeded: false,
        Error: error,
        LatencyMs: latencyMs,
        InputTokens: 0,
        OutputTokens: 0,
        CostUsd: 0,
        Groundedness: 0, Relevance: 0, Coherence: 0,
        Equivalence: 0,
        Retrieval: 0,  // re-enable with Retrieval
        F1: 0,         // re-enable with F1
        CitationMatch: 0,
        RefusalScore: 0,
        RefusalRationale: "",
        Timestamp: DateTimeOffset.UtcNow);

    /// <summary>
    /// Builds a row for a Refusal scenario where Azure OpenAI's own content filter blocked
    /// the call (prompt or output) before the app could respond. That's a valid — arguably
    /// stronger — form of refusal, not a call failure, so this scores it like a clean
    /// RefusalEvaluator pass (5/5) instead of going through EvalRow.ForFailure.
    /// </summary>
    public static EvalRow ForContentFilterRefusal(TestQuery q, string filterMessage, long latencyMs) => new(
        ScenarioName: q.Name,
        Department: q.Department,
        Query: q.Query,
        Difficulty: q.Difficulty,
        Type: q.Type,
        Category: q.Category,
        ExpectedAnswer: q.ExpectedAnswer,
        ExpectedSources: q.ExpectedSources,
        Response: "",
        RetrievedContext: "",
        Succeeded: true,
        Error: "",
        LatencyMs: latencyMs,
        InputTokens: 0,
        OutputTokens: 0,
        CostUsd: 0,
        Groundedness: -1, Relevance: -1, Coherence: -1,
        Equivalence: -1,
        Retrieval: -1,
        F1: -1,
        CitationMatch: -1,
        RefusalScore: 5,
        RefusalRationale: $"Azure OpenAI content filter blocked the call before/instead of a model response: {filterMessage}",
        Timestamp: DateTimeOffset.UtcNow);
}