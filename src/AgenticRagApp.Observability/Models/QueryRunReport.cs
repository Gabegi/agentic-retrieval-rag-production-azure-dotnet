namespace AgenticRagApp.Observability.Reports;

public record QueryRunReport(
    string                 RunId,
    DateTimeOffset         Timestamp,
    string                 Question,
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
    string?                Category,
    long                   LatencyMs,
    long                   InputTokens,
    long                   OutputTokens,
    long                   TotalTokens,
    // Estimated tokens in RetrievedContext (see AgenticRagApp.Querying.Services.
    // ContextTokenEstimator) — the query-time cost driver first-split-design.md §5 asks
    // to track directly, distinct from InputTokens (which also carries system-
    // instruction/prompt overhead).
    long                   ContextTokens,
    float?                 Temperature,
    int?                   MaxOutputTokens,
    float?                 TopP,
    int?                   TopK,
    float?                 FrequencyPenalty,
    float?                 PresencePenalty,
    long?                  Seed,
    string?                ResponseFormat,
    IReadOnlyList<string>? StopSequences);
