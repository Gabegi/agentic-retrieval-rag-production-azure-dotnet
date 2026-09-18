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
    private readonly IEmbeddingClient          _embeddingClient;
    private readonly IVectorCache              _vectorCache;

    private const int MaxParallelism = 4;
    private const int BatchSize = 100;    // one request per batch instead of one per chunk

    // No IndexerConfig: the width vectors are judged against is no longer a configured value but
    // the live index field width, read once at preflight and passed per call (D201). This service
    // is a singleton, so holding a width here would also mean holding one run's value for the
    // lifetime of the host.
    public EmbeddingService(
        IEmbeddingClient                              embeddingClient,
        IVectorCache                                  vectorCache,
        ILogger<EmbeddingService>                     logger)
    {
        _logger          = logger;
        _embeddingClient = embeddingClient;
        _vectorCache     = vectorCache;
    }

    public async Task<EmbeddingRunResult> EmbedDocumentsAsync(
        IEnumerable<ChunkObject> documents,
        int indexVectorDimensions,
        CancellationToken ct = default)
    {
        var cache = new VectorCacheGateway(_vectorCache, indexVectorDimensions);
        // EmbeddingService's own logger, deliberately - see BatchEmbedder's comment on the
        // log category.
        var batchEmbedder = new BatchEmbedder(_embeddingClient, indexVectorDimensions, _logger);

        var docList = documents.ToList();

        // Three wall-clocks, so the report can say how much of the embed step was the paid API
        // phase and how much was cache I/O (2026-09-15), and since 2026-09-18 (D203 M2b) which
        // cache PASS: the read clock brackets the split, the write clock the write-back, and
        // CachePhaseMs stays their sum so its meaning on every earlier report is unchanged. The
        // caller's TotalEmbeddingDurationMs wraps this whole method; these are the split of it.
        var readClock = System.Diagnostics.Stopwatch.StartNew();

        // A chunk whose content hash is already cached gets its vector back for free - no
        // embedding API call. Only genuinely new/changed chunks (within an updated document,
        // typically just the pages that actually changed) go on to the batch embedder below.
        var readPass = await cache.SplitAsync(docList, ct);
        readClock.Stop();
        var (cached, toEmbed, readOps) = readPass;

        _logger.LogInformation(
            "Embedding {ToEmbed} of {Total} documents in batches of {BatchSize} ({CacheHits} reused from vector cache)",
            toEmbed.Count, docList.Count, BatchSize, cached.Count);

        var apiClock     = System.Diagnostics.Stopwatch.StartNew();
        var semaphore    = new SemaphoreSlim(MaxParallelism);
        var tasks        = toEmbed.Chunk(BatchSize).Select(batch => batchEmbedder.EmbedBatchAsync(batch, semaphore, ct)).ToList();
        var batchResults = await Task.WhenAll(tasks);
        apiClock.Stop();
        var freshResults = batchResults.SelectMany(b => b.Results).ToArray();

        var writeClock = System.Diagnostics.Stopwatch.StartNew();
        var writePass  = await cache.WriteFreshAsync(freshResults, ct);
        writeClock.Stop();
        var writeOps = writePass.Operations;

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
            CachePhaseMs = readClock.ElapsedMilliseconds + writeClock.ElapsedMilliseconds,
            // The split of CachePhaseMs and what happened inside each half (D203 §3). Hashing is
            // inside the read clock, as it always was - HashMs says how much of it.
            CacheReadMs         = readClock.ElapsedMilliseconds,
            CacheWriteMs        = writeClock.ElapsedMilliseconds,
            HashMs              = readPass.HashMs,
            CacheBytesRead      = readPass.BytesRead,
            CacheBytesWritten   = writePass.BytesWritten,
            VectorDeserializeMs = readPass.DeserializeMs,
            VectorClassifyMs    = readPass.ClassifyMs,
            CacheWritesSkipped  = writePass.Skipped,
            GetHitLatency       = readPass.GetHitLatency,
            GetMissLatency      = readPass.GetMissLatency,
            PutLatency          = writePass.PutLatency,
            // What the reused vectors would have billed: the stored count of the exact text a
            // fresh embed would have sent. The tokens the cache saved, in the model's own unit.
            CacheHitTokens = cached.Sum(d => (long)d.TokenCount),
            // Both cache passes' round trips, and the width they ran at - the divisors for
            // CachePhaseMs. Summed from what the passes counted, never from docList.Count:
            // action 2 would stop probing hashes a listing already ruled out, and the derived
            // figure would not notice.
            CacheOperations  = readOps + writeOps,
            CacheParallelism = VectorCacheGateway.MaxCacheParallelism,
        };
    }
}
