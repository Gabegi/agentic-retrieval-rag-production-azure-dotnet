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
public interface IEmbeddingClient
{
    Task<(float[][] Vectors, int Retries, long? InputTokens)> EmbedWithRetryAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}
