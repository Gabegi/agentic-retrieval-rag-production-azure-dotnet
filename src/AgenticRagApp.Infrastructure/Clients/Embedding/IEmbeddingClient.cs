namespace AgenticRagApp.Infrastructure.Clients.Embedding;

// Generic wrapper around IEmbeddingGenerator — one embedding API call with retry/backoff
// on throttling and transient failures. Batching, truncation, per-item logging, dimension
// checks, and caching decisions are all caller-specific and stay in the Indexing projects.
//
// InputTokens is the call's billed input tokens as the SERVICE reported them in the response
// usage — null when the response carried none (blank-means-blank, observability plan 1.6,
// decided 2026-08-26: no local estimate ever substitutes). Returned rather than metered here
// because this project cannot reference Observability (it would be a cycle); the callers
// record the meter and carry the number into their stage reports.
// ThrottledRetries is the subset of Retries caused by a 429 specifically (2026-09-15). Retries
// alone cannot answer "were we rate-limited": it also counts 5xx, dropped connections and request
// timeouts, and those have opposite remedies - a 429 spike means the deployment's TPM is the
// constraint (raise quota, or lower parallelism), while a 5xx spike means the service was
// unwell and waiting is the only fix. What a full re-embed actually costs is wall-clock and
// throttling, not dollars, which is why this is counted rather than estimated.
public interface IEmbeddingClient
{
    Task<(float[][] Vectors, int Retries, int ThrottledRetries, long? InputTokens)> EmbedWithRetryAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}
