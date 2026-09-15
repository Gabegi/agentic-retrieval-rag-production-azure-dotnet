namespace RagApp.Evaluation.Tests.Models;

public record EvalRow(
    // Identity
    string          ScenarioName,
    string          Department,        // Afdeling — for filtering/reporting
    string          Query,              // Vraag
    string          Difficulty,         // Lastigheid
    ScenarioType    Type,               // Answer or Refusal — which metrics below are actually scored
    string          Category,           // protocol / buiten_scope / medisch_advies / promptinjectie / ...

    // --- The agentic-retrieval question -------------------------------------------------
    // Capability/MinSubQueries come from the dataset (what this question NEEDS);
    // SubQueryCount/SubQueries/DistinctDocumentsCited are measured (what the run DID).
    // Reading a run per Capability is what separates "agentic retrieval helps" from "the
    // synthesis model is good": a SingleLookup row can only get slower and dearer from
    // planning, so if the means only move there, nothing was gained. A Decomposition or
    // MultiHop row answered with SubQueryCount == 1 was never given the chance to benefit,
    // whatever it scored — that row is measuring the model's memory, not retrieval.
    AgenticCapability Capability,
    int             MinSubQueries,      // dataset-side lower bound on distinct searches needed
    int             SubQueryCount,      // measured: searches the knowledge base actually ran (-1 = not reported)
    string          SubQueries,         // measured: those search texts, ' | '-joined, so a decomposition can be read
    int             DistinctDocumentsCited, // measured: distinct documents behind the citations — a cross-document answer that cites one document did not join anything

    // Golden truth (what we expected)
    string          ExpectedAnswer,     // Antwoord
    string          ExpectedSources,    // Bronnen

    // Actual output
    string          Response,
    string          RetrievedContext,
    bool            Succeeded,
    string          Error,
    // Why the app answered the way it did. Without these two, a run where retrieval found
    // nothing and a run where the retrieved references failed to map are byte-identical in
    // this file — the 2026-08-11 eval had 31 of 32 answerable questions on the fallback with
    // no way to tell them apart. stop = a real answer; no_relevant_answer = Search returned
    // nothing; references_unmappable = Search returned references the mapper dropped (a
    // knowledge-source SourceDataFields mismatch, not an out-of-scope question).
    string          FinishReason,
    // Mapped chunks fed to synthesis. On a blocked row this carries the raw reference count
    // Search returned instead, so ChunksRetrieved > 0 alongside a fallback answer points at
    // mapping rather than retrieval.
    int             ChunksRetrieved,

    // Performance
    long            LatencyMs,
    long            InputTokens,
    long            OutputTokens,
    double          CostUsd,            // (InputTokens × inputPrice + OutputTokens × outputPrice) / 1M
    long            ContextTokens,      // estimated tokens in RetrievedContext (see ContextTokenEstimator) — the query-time cost driver first-split-design.md §5 asks to track directly, not inferred from InputTokens (which also carries system-instruction/prompt overhead)

    // Scores — Answer scenarios only (−1 = not scored, e.g. a Refusal scenario)
    double          Groundedness,       // 1-5  LLM — response grounded in retrieved context?
    double          Relevance,          // 1-5  LLM — response relevant to the question?
    double          Coherence,          // 1-5  LLM — response coherent and well-formed?
    double          Equivalence,        // 1-5  LLM — same meaning as expected answer?
    double       Retrieval,          // 1-5  LLM — was the right context fetched?  (re-enable with Retrieval)
    double       F1,                 // 0-1  NLP — token overlap vs expected answer (re-enable with F1)
    double       CitationMatch,      // 0-1  deterministic — fraction of ExpectedSources doc IDs present in Citations (= document-level recall over the retrieved references); -1 if ExpectedSources has no matchable doc ID
    // Retrieval rank (2026-09-15, RetrievalRankMetrics). The golden set labels DOCUMENTS, not
    // chunks, so these are document-level: a reference is relevant when its document is one of
    // ExpectedSources. Measured on the production retrieval path (agentic planner, hybrid,
    // semantic reranker) - there is no vector-only harness.
    int          ReferencesRetrieved, // k: references the knowledge base returned, before neighbor expansion (ChunksRetrieved is after); -1 = not reported
    int          FirstRelevantRank,   // 1-based rank, by the service's reranker score, of the first reference whose document is expected; 0 = none of the expected documents was retrieved; -1 = not scorable
    double       ReciprocalRank,      // 1 / FirstRelevantRank (0 when none, -1 when not scorable) — the per-row term of MRR: how HIGH the right document sat, not just whether it was in the set
    // recall@k over the same ranking. The GAP between the two is the point (D111): in the top 50
    // but not the top 5 = a ranking problem; in neither = never retrieved, which is a chunking
    // problem no reranker fixes. Where ReferencesRetrieved < 50, RecallAt50 is recall over the
    // whole returned set (the production path sets no top-k) — containment, not rank.
    double       RecallAt5,           // 0-1 deterministic — expected documents found in the first 5 references; -1 = not scorable
    double       RecallAt50,          // 0-1 deterministic — same at k=50; always >= RecallAt5
    int          UngroundedNumbers,  // count, deterministic — numeric literals in Response absent from RetrievedContext (NumericGroundingGuard): a correct-but-uncited figure is model memory wearing this context's citations; -1 = not scored (Refusal/failure rows)

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
        Capability: q.Capability,
        MinSubQueries: q.MinSubQueries,
        SubQueryCount: -1,
        SubQueries: "",
        DistinctDocumentsCited: 0,
        ExpectedAnswer: q.ExpectedAnswer,
        ExpectedSources: q.ExpectedSources,
        Response: "",
        RetrievedContext: "",
        Succeeded: false,
        Error: error,
        FinishReason: "call_failed",
        ChunksRetrieved: 0,
        LatencyMs: latencyMs,
        InputTokens: 0,
        OutputTokens: 0,
        CostUsd: 0,
        ContextTokens: 0,
        Groundedness: 0, Relevance: 0, Coherence: 0,
        Equivalence: 0,
        Retrieval: 0,  // re-enable with Retrieval
        F1: 0,         // re-enable with F1
        CitationMatch: 0,
        ReferencesRetrieved: 0,
        FirstRelevantRank: 0,
        ReciprocalRank: 0,
        RecallAt5: 0,
        RecallAt50: 0,
        UngroundedNumbers: -1,
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
        Capability: q.Capability,
        MinSubQueries: q.MinSubQueries,
        SubQueryCount: -1,
        SubQueries: "",
        DistinctDocumentsCited: 0,
        ExpectedAnswer: q.ExpectedAnswer,
        ExpectedSources: q.ExpectedSources,
        Response: "",
        RetrievedContext: "",
        Succeeded: true,
        Error: "",
        FinishReason: "content_filter",
        ChunksRetrieved: 0,
        LatencyMs: latencyMs,
        InputTokens: 0,
        OutputTokens: 0,
        CostUsd: 0,
        ContextTokens: 0,
        Groundedness: -1, Relevance: -1, Coherence: -1,
        Equivalence: -1,
        Retrieval: -1,
        F1: -1,
        CitationMatch: -1,
        ReferencesRetrieved: -1,
        FirstRelevantRank: -1,
        ReciprocalRank: -1,
        RecallAt5: -1,
        RecallAt50: -1,
        UngroundedNumbers: -1,
        RefusalScore: 5,
        RefusalRationale: $"Azure OpenAI content filter blocked the call before/instead of a model response: {filterMessage}",
        Timestamp: DateTimeOffset.UtcNow);
}