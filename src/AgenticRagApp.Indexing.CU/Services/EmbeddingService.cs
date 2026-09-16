using Microsoft.Extensions.Logging;
using AgenticRagApp.Infrastructure.Clients.Embedding;
using AgenticRagApp.Infrastructure.Configuration;
using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

// Run-level orchestration of the embed stage: cache split, batching, the two wall-clocks, and
// the run totals the report reads. The per-batch rules (truncation to the model's input limit,
// response validation) live in BatchEmbedder and the bulk cache passes in VectorCacheGateway,
// both under Services/Embedding/ - moved out 2026-09-16.
public class EmbeddingService : IEmbeddingService
{
    private readonly ILogger<EmbeddingService> _logger;
    private readonly VectorCacheGateway        _cache;
    private readonly BatchEmbedder             _batchEmbedder;

    private const int MaxParallelism = 4;
    private const int BatchSize = 100;    // one request per batch instead of one per chunk

    public EmbeddingService(
        IEmbeddingClient                              embeddingClient,
        IVectorCache                                  vectorCache,
        IndexerConfig                                 config,
        ILogger<EmbeddingService>                     logger)
    {
        _logger        = logger;
        _cache         = new VectorCacheGateway(vectorCache, config.OpenAiEmbeddingDimensions);
        // EmbeddingService's own logger, deliberately - see BatchEmbedder's comment on the
        // log category.
        _batchEmbedder = new BatchEmbedder(embeddingClient, config.OpenAiEmbeddingDimensions, logger);
    }

    public async Task<EmbeddingRunResult> EmbedDocumentsAsync(
        IEnumerable<ChunkObject> documents,
        CancellationToken ct = default)
    {
        var docList = documents.ToList();

        // Two wall-clocks, so the report can say how much of the embed step was the paid API
        // phase and how much was cache I/O (2026-09-15). The caller's TotalEmbeddingDurationMs
        // wraps this whole method; these two are the split of it.
        var cacheClock = System.Diagnostics.Stopwatch.StartNew();

        // A chunk whose content hash is already cached gets its vector back for free - no
        // embedding API call. Only genuinely new/changed chunks (within an updated document,
        // typically just the pages that actually changed) go on to the batch embedder below.
        var (cached, toEmbed) = await _cache.SplitAsync(docList, ct);
        cacheClock.Stop();

        _logger.LogInformation(
            "Embedding {ToEmbed} of {Total} documents in batches of {BatchSize} ({CacheHits} reused from vector cache)",
            toEmbed.Count, docList.Count, BatchSize, cached.Count);

        var apiClock     = System.Diagnostics.Stopwatch.StartNew();
        var semaphore    = new SemaphoreSlim(MaxParallelism);
        var tasks        = toEmbed.Chunk(BatchSize).Select(batch => _batchEmbedder.EmbedBatchAsync(batch, semaphore, ct)).ToList();
        var batchResults = await Task.WhenAll(tasks);
        apiClock.Stop();
        var freshResults = batchResults.SelectMany(b => b.Results).ToArray();

        cacheClock.Start();
        await _cache.WriteFreshAsync(freshResults, ct);
        cacheClock.Stop();

        _logger.LogInformation("Embedding complete — {Fresh} embedded, {Cached} reused", freshResults.Length, cached.Count);

        return new EmbeddingRunResult(
            Documents:        cached.Concat(freshResults.Select(r => r.Document)),
            ChunksTruncated:  freshResults.Count(r => r.Truncated),
            EmbeddingRetries: batchResults.Sum(b => b.Retries),
            VectorDimErrors:  freshResults.Count(r => r.DimError),
            CacheHits:        cached.Count)
        {
            // Null when NO batch reported usage (blank), a number when any did. A mix of
            // reporting and non-reporting batches sums the reporting ones - the meter and this
            // field say the same thing by construction.
            TotalInputTokens = batchResults.Any(b => b.InputTokens is not null)
                ? batchResults.Sum(b => b.InputTokens ?? 0) : null,
            EmptyVectors     = freshResults.Count(r => r.EmptyVector),
            ThrottledRetries = batchResults.Sum(b => b.ThrottledRetries),
            ApiPhaseMs   = apiClock.ElapsedMilliseconds,
            CachePhaseMs = cacheClock.ElapsedMilliseconds,
            // What the reused vectors would have billed: the stored count of the exact text a
            // fresh embed would have sent. The tokens the cache saved, in the model's own unit.
            CacheHitTokens = cached.Sum(d => (long)d.TokenCount),
        };
    }
}
