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
    //
    // Public because it is the divisor in the only formula that reads the cache timings -
    // ms/op = CachePhaseMs × P / operations - and on 2026-09-17 a review had to INFER it from
    // the answer (17,224 ms is 34.5 ms/op at 8 and 138 at 32, and only the first is inside the
    // archive's 36-48 band), which cannot distinguish "the change never deployed" from "the
    // store stopped scaling at 32". It rides EmbeddingRunResult.CacheParallelism onto the run
    // report so the divisor is read, not guessed (D197 action 4).
    public const int MaxCacheParallelism = 32;

    private readonly IVectorCache _vectorCache;
    private readonly int          _expectedDimensions;

    public VectorCacheGateway(IVectorCache vectorCache, int expectedDimensions)
    {
        _vectorCache        = vectorCache;
        _expectedDimensions = expectedDimensions;
    }

    // Splits by vector-cache hit/miss. A cached vector whose length no longer matches the
    // configured embedding dimensions (model/config changed since it was cached), or that is
    // empty (see VectorHealth.Classify), is treated as a miss rather than trusted blindly.
    //
    // Operations is the blob round trips this pass actually made, incremented at the call, not
    // derived from docs.Count afterwards. Both passes return it rather than accumulating on the
    // instance because EmbeddingService is a singleton (ServiceCollectionExtensions.cs) - a
    // field here would sum every run since host start.
    public async Task<(List<ChunkObject> Cached, List<ChunkObject> ToEmbed, int Operations)> SplitAsync(
        List<ChunkObject> docs, CancellationToken ct)
    {
        var cached     = new ConcurrentBag<ChunkObject>();
        var toEmbed    = new ConcurrentBag<ChunkObject>();
        var operations = 0;

        await Parallel.ForEachAsync(
            docs,
            new ParallelOptions { MaxDegreeOfParallelism = MaxCacheParallelism, CancellationToken = ct },
            async (doc, token) =>
            {
                Interlocked.Increment(ref operations);
                var vector = await _vectorCache.TryGetAsync(doc.ContentHash, token);
                if (vector is { } v && VectorHealth.Classify(v, _expectedDimensions) is VectorVerdict.Healthy)
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

        return (cached.ToList(), toEmbed.ToList(), operations);
    }

    // Writes every freshly-embedded chunk's vector back to the cache, keyed by content hash,
    // so the next run that touches an unchanged chunk with the same hash gets a cache hit
    // instead of paying to re-embed it. Skips dimension-mismatched and empty vectors - not
    // worth caching a result we already know is wrong.
    // Returns the blob round trips it made: the existence check plus one per PUT, or 0 when
    // there was nothing to write. Counting them here is what keeps ms/op honest across action
    // 1c - a PUT was TWO operations until 2026-09-16 (SetAsync created the container first) and
    // is one now, so any ops figure derived from chunk counts silently changed basis across the
    // very change it was meant to verify (D197 §5).
    public async Task<int> WriteFreshAsync(IReadOnlyList<EmbedChunkResult> results, CancellationToken ct)
    {
        var writable = results
            .Where(r => !r.DimError && !r.EmptyVector && r.Document.ContentVector is not null)
            .ToList();
        if (writable.Count == 0) return 0;

        // Once per run, not once per PUT (2026-09-16, D197 action 1c). VectorCache.SetAsync used
        // to call CreateIfNotExistsAsync before every upload - a second round trip per write at
        // the same ~40 ms, so 255 extra ops on a warm run and 3,703 on a cold one. An existence
        // check rather than a create: Terraform owns the container (infra/storage.tf), and a
        // silent auto-create is how a misnamed container once went unnoticed - see IBlobStore.
        await _vectorCache.AssertContainerExistsAsync(ct);
        var operations = 1;

        await Parallel.ForEachAsync(
            writable,
            new ParallelOptions { MaxDegreeOfParallelism = MaxCacheParallelism, CancellationToken = ct },
            (r, token) =>
            {
                Interlocked.Increment(ref operations);
                return new ValueTask(_vectorCache.SetAsync(r.Document.ContentHash, r.Document.ContentVector!, token));
            });

        return operations;
    }
}
