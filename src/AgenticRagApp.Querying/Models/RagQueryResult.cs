namespace AgenticRagApp.Querying.Models;

public record RagQueryResult(
    string                 Answer,
    string                 RetrievedContext,
    string                 SystemInstructions,
    int                    ChunksRetrieved,
    string                 OperationName,
    string                 ProviderName,
    string                 ServerAddress,
    int                    ServerPort,
    string                 ConversationId,
    string                 Model,
    string                 FinishReason,
    // Business-facing refusal category (golden-questions dataset, 2026-08-06): "privacy",
    // "promptinjectie", "buiten_scope" for the three guard/threshold paths this code can
    // actually detect. Null on a normal answer (FinishReason "stop"). The full taxonomy in
    // that dataset is richer (autorisatie, medisch_advies, juridisch_advies,
    // financieel_advies, overmatige_extractie, misbruik, observability, and multi-label
    // combinations like "privacy / autorisatie") but those aren't guard-enforced - the model
    // self-polices them via AnswerInstructions wording only, so there's no code signal to
    // derive a category from when one of those fires. See
    // docs/2608/260806/po-open-questions.md.
    string?                Category,
    long                   LatencyMs,
    long                   InputTokens,
    long                   OutputTokens,
    long                   TotalTokens,
    // Estimated tokens in RetrievedContext (see ContextTokenEstimator) — the query-time
    // cost driver first-split-design.md §5 asks to track directly, distinct from
    // InputTokens (which also carries system-instruction/prompt overhead).
    long                   ContextTokens,
    float?                 Temperature,
    int?                   MaxOutputTokens,
    float?                 TopP,
    int?                   TopK,
    float?                 FrequencyPenalty,
    float?                 PresencePenalty,
    long?                  Seed,
    string?                ResponseFormat,
    IReadOnlyList<string>? StopSequences,
    IReadOnlyList<Citation> Citations,
    // The searches the knowledge base planned and ran for this question, in order (see
    // KnowledgeBaseActivitySummary.CollectSubQueries). One entry = the planner produced a
    // single search, so this request got nothing a plain search could not have done; several
    // entries = it decomposed the question. Empty on a guard-blocked row, where no retrieval
    // quality is being measured anyway.
    IReadOnlyList<string>? SubQueries = null)
{
    // The retrieved set as a ranking, for the eval's rank metrics (2026-09-15).
    //
    // ReferencesRetrieved is k: the references the knowledge base returned and the mapper kept,
    // BEFORE neighbor expansion - ChunksRetrieved is the count after it, i.e. what synthesis
    // was handed. RetrievedDocumentRanking is one document id per reference, ordered by the
    // service's reranker score (ties and missing scores keep return order), so "rank of the
    // first expected document" is the service's own ranking, not ours. Citations are grouped
    // per (document, page) and cannot carry this - two references from one page collapse.
    //
    // Init properties so the positional constructor - and every test building one - stays
    // untouched. 0 / null on a guard-blocked row.
    public int                    ReferencesRetrieved      { get; init; }
    public IReadOnlyList<string>? RetrievedDocumentRanking { get; init; }
    // The reranker score behind each entry of RetrievedDocumentRanking, same order, same length
    // (2026-09-23, D228 step 3). Null entry = the service sent no score for that reference.
    // Read by the eval row only; QueryResponse.From does not map it, so - like the ranking - it
    // never reaches an HTTP caller (QueryResponseTests pins that).
    public IReadOnlyList<float?>? RetrievedRerankerScores  { get; init; }
    // The document behind each block of RetrievedContext, in block order, same length as the
    // number of blocks (2026-09-23, D228 step 3b). Direct hits AND neighbours, after the context
    // cap - so "is the expected document in what the judges read" is a lookup, not a text
    // search. Eval-row only, like the two above; QueryResponse.From does not map it.
    public IReadOnlyList<string>?  ContextDocumentIds       { get; init; }
}
