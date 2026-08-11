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
    IReadOnlyList<Citation> Citations);
