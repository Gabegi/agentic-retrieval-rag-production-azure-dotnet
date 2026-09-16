using System.Collections.Concurrent;
using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Observability;

namespace AgenticRagApp.Indexing.CU.Services;

// A run's two passes over the vector cache: read every chunk before embedding, write back the
// ones that had to be embedded. Split out of EmbeddingService (2026-09-16), and kept as ONE
// class rather than a reader and a writer because both sides answer to the same concurrency
// knob - splitting them would mean two copies of MaxCacheParallelism drifting apart.
//
// Not the cache itself: VectorCache owns the blob layout and the per-entry semantics. This is
// the run-level shape on top of it - bulk, parallel, and aware of what makes a cached vector
// unusable.
public sealed class VectorCacheGateway
{
    // Cache reads/writes are just blob GETs/PUTs, not paid API calls. Same knob shape as
    // ExtractionService.MaxExtractionParallelism.
    //
    // 8 until 2026-09-16, set to keep cache I/O "off the critical path". It WAS the critical
    // path: on 260915/1 the cache phase was 19,994 ms against 793 ms of API - 3,958 blob ops at
    // ~40 ms each, 8 at a time - and the run archive bounds that latency at 36-48 ms/op on 17
    // warm runs (D196 §4a). Raised to 32 as D197 action 1; expected cache phase on that run
    // shape is 3,958 × 40.4 / 32 ≈ 5.0 s IF per-op latency holds at 32-wide, which is the
    // thing the next run's VectorCacheDurationMs verifies (D197 §5). Not raised further until it
    // does.
    private const int MaxCacheParallelism = 32;

    private readonly IVectorCache _vectorCache;
    private readonly int          _expectedDimensions;

    public VectorCacheGateway(IVectorCache vectorCache, int expectedDimensions)
    {
        _vectorCache        = vectorCache;
        _expectedDimensions = expectedDimensions;
    }

    // Splits by vector-cache hit/miss. A cached vector whose length no longer matches the
    // configured embedding dimensions (model/config changed since it was cached), or that is
    // empty (see VectorHealth.IsEmptyVector), is treated as a miss rather than trusted blindly.
    public async Task<(List<ChunkObject> Cached, List<ChunkObject> ToEmbed)> SplitAsync(
        List<ChunkObject> docs, CancellationToken ct)
    {
        var cached  = new ConcurrentBag<ChunkObject>();
        var toEmbed = new ConcurrentBag<ChunkObject>();

        await Parallel.ForEachAsync(
            docs,
            new ParallelOptions { MaxDegreeOfParallelism = MaxCacheParallelism, CancellationToken = ct },
            async (doc, token) =>
            {
                var vector = await _vectorCache.TryGetAsync(doc.ContentHash, token);
                if (vector is { } v && v.Length == _expectedDimensions && !VectorHealth.IsEmptyVector(v))
                {
                    doc.ContentVector = v;
                    cached.Add(doc);
                    Instrumentation.VectorCacheHits.Add(1);
                }
                else
                {
                    toEmbed.Add(doc);
                }
            });

        return (cached.ToList(), toEmbed.ToList());
    }

    // Writes every freshly-embedded chunk's vector back to the cache, keyed by content hash,
    // so the next run that touches an unchanged chunk with the same hash gets a cache hit
    // instead of paying to re-embed it. Skips dimension-mismatched and empty vectors - not
    // worth caching a result we already know is wrong.
    public async Task WriteFreshAsync(IReadOnlyList<EmbedChunkResult> results, CancellationToken ct)
    {
        var writable = results
            .Where(r => !r.DimError && !r.EmptyVector && r.Document.ContentVector is not null)
            .ToList();
        if (writable.Count == 0) return;

        // Once per run, not once per PUT (2026-09-16, D197 action 1c). VectorCache.SetAsync used
        // to call CreateIfNotExistsAsync before every upload - a second round trip per write at
        // the same ~40 ms, so 255 extra ops on a warm run and 3,703 on a cold one. An existence
        // check rather than a create: Terraform owns the container (infra/storage.tf), and a
        // silent auto-create is how a misnamed container once went unnoticed - see IBlobStore.
        await _vectorCache.AssertContainerExistsAsync(ct);

        await Parallel.ForEachAsync(
            writable,
            new ParallelOptions { MaxDegreeOfParallelism = MaxCacheParallelism, CancellationToken = ct },
            (r, token) => new ValueTask(_vectorCache.SetAsync(r.Document.ContentHash, r.Document.ContentVector!, token)));
    }
}
