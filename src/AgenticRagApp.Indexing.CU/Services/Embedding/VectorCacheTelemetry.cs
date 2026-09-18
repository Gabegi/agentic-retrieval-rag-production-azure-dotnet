using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Indexing.CU.Services;

// What the vector cache measures about itself, per call and per pass (2026-09-18, D203 §3).
//
// Until now the cache phase was ONE number, VectorCacheDurationMs, and every reading of it was
// a derivation: ms/op = wall-clock × P / operations, with no way to say how much of an "op"
// was the round trip, the JSON, or the health check. These records carry the measured pieces
// back to EmbeddingRunResult and the run report so the split is read, not inferred.
//
// All timings are wall-clock of the thing named and nothing else: the SDK call is bracketed
// without the deserialize, the deserialize without the classify. That is what makes the
// per-step numbers sum to the pass clocks, which D203 §3 makes the acceptance test - if they do
// not add up, the clocks bracket the wrong thing and nothing built on them counts.

// One cache read that returned a body. Bytes is the blob's content length as downloaded;
// DeserializeTicks is Stopwatch ticks spent turning the body into a float[] - a JSON parse until
// 2026-09-18, a float32 cast-and-copy since (D203 O2) - CPU on the host, not the store, and the
// reason it is separate from the GET.
public sealed record CachedVector(float[] Vector, long Bytes, long DeserializeTicks)
{
    // For callers and tests that only have a vector: the telemetry reads as unmeasured zeros.
    public CachedVector(float[] vector) : this(vector, 0, 0) { }
}

// The eviction pass split into the two things it does: ONE listing of the cache prefix, then
// one delete per orphan. Listed is every blob under the prefix (the live cache size);
// BlobBytesTotal and BlobBytesP50 come from the listing's content lengths - the first
// measurement of what a cache entry actually weighs on disk (D203 §2 estimated 35-40 KB
// per JSON-encoded 3,072-float vector; this is the number that confirms or kills it). Null
// when the listing carried no sizes.
public sealed record VectorCacheEviction(
    int   Listed,
    int   Deleted,
    long  ListMs,
    long  DeleteMs,
    long? BlobBytesTotal,
    long? BlobBytesP50,
    // Per-delete wall-clock, SDK call only (D203 §6c). Null when nothing was deleted. Covers
    // legacy deletes too on the one run that has them.
    LatencySummary? DeleteLatency = null,
    // JSON-era `.json` entries swept because the format changed (D203 O2), NOT orphans: their
    // hashes may well be live. Non-zero on exactly one run after the format change, then 0.
    int LegacyDeleted = 0)
{
    public static readonly VectorCacheEviction None = new(0, 0, 0, 0, null, null);
}

// The read pass. Cached / ToEmbed / Operations are what the pass has always returned;
// the Deconstruct keeps every existing `var (cached, toEmbed, ops) = ...` caller compiling.
//
// HashMs is one forced evaluation of ContentHash for every chunk, timed on its own before the
// probes start (D203 M1). ContentHash is a computed property re-hashed on every access, so this
// is the cost of ONE pass; the run makes at least four (split, write, snapshot, artifact).
// BytesRead sums the content length of every GET that returned a body. DeserializeMs and
// ClassifyMs are summed CPU time across the parallel probes, so they can exceed wall-clock.
//
// GetHitLatency / GetMissLatency are the per-probe wall-clock of TryGetAsync with the JSON parse
// subtracted, so they read as the store's round trip by kind (D203 §6c). A hit carries a body, a
// miss does not: the gap between their medians is what the payload costs per GET.
public sealed record VectorCacheReadPass(
    List<ChunkObject> Cached,
    List<ChunkObject> ToEmbed,
    int               Operations,
    long              HashMs,
    long              BytesRead,
    long              DeserializeMs,
    long              ClassifyMs,
    LatencySummary?   GetHitLatency  = null,
    LatencySummary?   GetMissLatency = null)
{
    public void Deconstruct(out List<ChunkObject> cached, out List<ChunkObject> toEmbed, out int operations)
    {
        cached     = Cached;
        toEmbed    = ToEmbed;
        operations = Operations;
    }
}

// The write pass. Operations is the existence check plus one per PUT, as before. Skipped is
// the fresh results NOT written because the embedder judged them wrong-width or unusable
// (D203 M4) - it should read 0 whenever VectorDimErrors and EmptyVectors do, and a non-zero
// here against zeros there is a bug in the gate, not in the model. BytesWritten sums the
// serialized payload of every PUT.
public sealed record VectorCacheWritePass(int Operations, int Skipped, long BytesWritten, LatencySummary? PutLatency = null);
