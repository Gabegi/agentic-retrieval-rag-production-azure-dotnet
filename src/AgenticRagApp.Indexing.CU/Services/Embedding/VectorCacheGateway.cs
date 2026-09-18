using System.Collections.Concurrent;
using System.Diagnostics;
using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Observability;
using AgenticRagApp.Observability.Reports;

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
    // 8, and 8 is MEASURED rather than assumed. Do not raise it again without a run that
    // contradicts the numbers below.
    //
    // History: 8 until 2026-09-16, when D197 action 1 raised it to 32 on the reasoning that the
    // cache phase was the critical path (260915/1: 19,994 ms of cache against 793 ms of API) and
    // that 3,958 ops at ~40 ms each, 8 at a time, would come down to ~5.0 s at 32-wide IF per-op
    // latency held. Run 9/260917/2 is the first to carry MaxCacheParallelism on the report, so it
    // is the first that can be read without guessing the divisor - and it says latency did not
    // hold. Same corpus, same day, 26 minutes apart:
    //
    //   P=8  (9/260917/1): 17,224 ms / 3,988 ops =  34.6 ms/op = 232 ops/s
    //   P=32 (9/260917/2): 23,913 ms / 3,985 ops = 192.0 ms/op = 167 ops/s
    //
    // Four times the concurrency, 28% LESS throughput. That is contention at the blob store, not
    // scaling, and it makes D197 action 1b (raise to 64) permanently answered: no. Reverted to 8
    // on 2026-09-17.
    //
    // Where the time actually is: on that same run embed_upload was 75.4 s, of which the cache is
    // 23.9 s and the embedding API 0.7 s. Roughly 50 s is the upload half and nothing measures it
    // (D200 R2). Tuning this constant further is tuning the smaller half of a stage whose larger
    // half is unmeasured.
    //
    // Public because it is the divisor in ms/op = CachePhaseMs × P / operations, and on
    // 2026-09-17 a review had to INFER it from the answer (17,224 ms is 34.5 ms/op at 8 or 138 at
    // 32, and only the first is inside the archive's 36-48 band), which cannot distinguish "the
    // change never deployed" from "the store stopped scaling". It rides
    // EmbeddingRunResult.CacheParallelism onto the run report so the divisor is read, not guessed
    // (D197 action 4) - which is what made the comparison above possible.
    //
    // What the P=1 measurement run settled (2026-09-18, run 9/260918/5, D203 §7b). Run /4 had
    // read the per-op latency for the first time: GET-hit p50 17.5 ms but p95 253 and max 618, so
    // the 20 s read pass was made by its slowest 5%, not its median - and that tail could have
    // been the store being spiky or eight continuations queueing on this host's single vCPU. One
    // force run at P=1 separated them: GET-hit p95 253 → 32.8 ms, GET-miss 182 → 16.9, PUT
    // 279 → 38, while DELETE (sequential on both runs, the control) did not move. The tail was
    // the host. Eight workers had been delivering 3.1× throughput; at P=1 latency the P=8 pass
    // would be 7.7 s against the 20 s measured. The fix that reading points at is less CPU per
    // probe, not a different width - VectorCache stores raw float32 since the same day (D203
    // O2). Back at 8, which D197 §5 measured as the store's sweet spot; do not move it again
    // without a run that says so.
    // History of this value: 8 (until 2026-09-16) → 32 (D197 action 1) → 8 (2026-09-17) → 1
    // (2026-09-18, one measurement run) → 8.
    public const int MaxCacheParallelism = 8;

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
    //
    // The pass is timed in pieces (2026-09-18, D203 §3): hashing before the probes start, then
    // per probe the GET (inside VectorCache), the JSON parse (returned by VectorCache) and the
    // health check (here). Parse and classify are summed across the parallel probes, so they
    // are CPU seconds on a 1-vCPU host, not wall-clock, and can legitimately exceed it.
    public async Task<VectorCacheReadPass> SplitAsync(List<ChunkObject> docs, CancellationToken ct)
    {
        using var span = Instrumentation.ActivitySource.StartActivity("vector_cache.read");

        // One forced evaluation per chunk, on its own clock (D203 M1). The array is reused by the
        // probes below so this pass hashes each chunk exactly once, which is also what it did
        // before - the measurement changes where the hash is computed, not how often.
        var hashClock = Stopwatch.StartNew();
        var hashes    = new string[docs.Count];
        for (var i = 0; i < docs.Count; i++) hashes[i] = docs[i].ContentHash;
        hashClock.Stop();

        var cached           = new ConcurrentBag<ChunkObject>();
        var toEmbed          = new ConcurrentBag<ChunkObject>();
        var hitMs            = new ConcurrentBag<double>();
        var missMs           = new ConcurrentBag<double>();
        var operations       = 0;
        long bytesRead       = 0;
        long deserializeTicks = 0;
        long classifyTicks   = 0;

        await Parallel.ForEachAsync(
            Enumerable.Range(0, docs.Count),
            new ParallelOptions { MaxDegreeOfParallelism = MaxCacheParallelism, CancellationToken = ct },
            async (i, token) =>
            {
                var doc = docs[i];
                Interlocked.Increment(ref operations);
                // Per-probe wall-clock with the parse subtracted, so it reads as the store's round
                // trip by kind (D203 §6c) - the report's stand-in for the histogram nobody can read.
                var probeStarted = Stopwatch.GetTimestamp();
                var read = await _vectorCache.TryGetAsync(hashes[i], token);
                var probeTicks = Stopwatch.GetTimestamp() - probeStarted;
                if (read is null)
                {
                    missMs.Add(TicksToMsExact(probeTicks));
                    toEmbed.Add(doc);
                    return;
                }
                hitMs.Add(TicksToMsExact(probeTicks - read.DeserializeTicks));

                Interlocked.Add(ref bytesRead,        read.Bytes);
                Interlocked.Add(ref deserializeTicks, read.DeserializeTicks);

                var classifyStarted = Stopwatch.GetTimestamp();
                var verdict = VectorHealth.Classify(read.Vector, _expectedDimensions);
                Interlocked.Add(ref classifyTicks, Stopwatch.GetTimestamp() - classifyStarted);

                if (verdict is VectorVerdict.Healthy)
                {
                    doc.ContentVector = read.Vector;
                    cached.Add(doc);
                    Instrumentation.VectorCacheHits.Add(1);
                }
                else
                {
                    toEmbed.Add(doc);
                }
            });

        var pass = new VectorCacheReadPass(
            Cached:        cached.ToList(),
            ToEmbed:       toEmbed.ToList(),
            Operations:    operations,
            HashMs:        hashClock.ElapsedMilliseconds,
            BytesRead:     bytesRead,
            DeserializeMs:  TicksToMs(deserializeTicks),
            ClassifyMs:     TicksToMs(classifyTicks),
            GetHitLatency:  LatencySummary.From(hitMs),
            GetMissLatency: LatencySummary.From(missMs));

        span?.SetTag("vector_cache.operations",     pass.Operations);
        span?.SetTag("vector_cache.hits",           pass.Cached.Count);
        span?.SetTag("vector_cache.bytes_read",     pass.BytesRead);
        span?.SetTag("vector_cache.hash_ms",        pass.HashMs);
        span?.SetTag("vector_cache.deserialize_ms", pass.DeserializeMs);
        span?.SetTag("vector_cache.classify_ms",    pass.ClassifyMs);
        return pass;
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
    public async Task<VectorCacheWritePass> WriteFreshAsync(IReadOnlyList<EmbedChunkResult> results, CancellationToken ct)
    {
        using var span = Instrumentation.ActivitySource.StartActivity("vector_cache.write");

        var writable = results
            .Where(r => !r.DimError && !r.EmptyVector && r.Document.ContentVector is not null)
            .ToList();
        var skipped = results.Count - writable.Count;
        span?.SetTag("vector_cache.skipped", skipped);
        if (writable.Count == 0) return new VectorCacheWritePass(Operations: 0, Skipped: skipped, BytesWritten: 0);

        // Once per run, not once per PUT (2026-09-16, D197 action 1c). VectorCache.SetAsync used
        // to call CreateIfNotExistsAsync before every upload - a second round trip per write at
        // the same ~40 ms, so 255 extra ops on a warm run and 3,703 on a cold one. An existence
        // check rather than a create: Terraform owns the container (infra/storage.tf), and a
        // silent auto-create is how a misnamed container once went unnoticed - see IBlobStore.
        await _vectorCache.AssertContainerExistsAsync(ct);
        var  operations   = 1;
        long bytesWritten = 0;
        var  putMs        = new ConcurrentBag<double>();

        await Parallel.ForEachAsync(
            writable,
            new ParallelOptions { MaxDegreeOfParallelism = MaxCacheParallelism, CancellationToken = ct },
            async (r, token) =>
            {
                Interlocked.Increment(ref operations);
                var putStarted = Stopwatch.GetTimestamp();
                var bytes = await _vectorCache.SetAsync(r.Document.ContentHash, r.Document.ContentVector!, token);
                putMs.Add(TicksToMsExact(Stopwatch.GetTimestamp() - putStarted));
                Interlocked.Add(ref bytesWritten, bytes);
            });

        span?.SetTag("vector_cache.operations",    operations);
        span?.SetTag("vector_cache.bytes_written", bytesWritten);
        return new VectorCacheWritePass(
            Operations: operations, Skipped: skipped, BytesWritten: bytesWritten, PutLatency: LatencySummary.From(putMs));
    }

    private static long TicksToMs(long stopwatchTicks) =>
        (long)(stopwatchTicks * 1000.0 / Stopwatch.Frequency);

    private static double TicksToMsExact(long stopwatchTicks) =>
        stopwatchTicks * 1000.0 / Stopwatch.Frequency;
}
