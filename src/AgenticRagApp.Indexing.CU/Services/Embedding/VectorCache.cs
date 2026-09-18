using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AgenticRagApp.Infrastructure.Clients.Blob;
using AgenticRagApp.Observability;
using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Indexing.CU.Services;

// One blob per content hash under pipeline-artifacts/vector-cache/ - not one shared file,
// so a read never races a concurrent write from another chunk and eviction (Stage 4) can
// delete individual orphaned entries without touching the rest.
//
// FORMAT: raw little-endian float32, 4 bytes per component, 12,288 B for a 3,072-wide vector
// (2026-09-18, D203 O2). Until then the blob was System.Text.Json text of the float[] -
// measured at 38,945 B median on runs 260918/1, /4 and /5, 3.17× the vector, plus 0.57 ms of
// parsing per hit. Why it mattered: run /5 (D203 §7b) showed the cache pass is bound by the
// HOST, not the store - eight probes on one vCPU queued behind each other's receive, copy and
// parse, turning a 12.7 ms median GET into a 253 ms p95. Fewer bytes and no parse per probe is
// the lever that reading points at. Blob suffix `.f32` so the format is visible in a listing and
// the JSON-era `.json` entries can be told apart and swept (EvictOrphanedAsync).
//
// Every SDK call here is bracketed by a stopwatch and recorded on Instrumentation.VectorCacheOpMs
// tagged by what the call was (2026-09-18, D203 M2a): get_hit, get_miss, put, delete, list. That
// histogram is the MEASURED per-operation latency by kind, p50 and p95, which replaces the
// derived "wall-clock × P / ops" that every reading of the cache phase relied on until now. The
// bracket is the SDK call only - the decode is timed separately and returned to the caller - so a
// slow decode cannot read as a slow blob.
public class VectorCache : IVectorCache
{
    private const string Prefix        = "vector-cache";
    private const string Suffix        = ".f32";
    private const string LegacySuffix  = ".json";
    private const int    BytesPerFloat = sizeof(float);

    private readonly BlobContainerClient _container;

    static VectorCache()
    {
        // The on-disk format is the host's native float32 layout, which is only a stable format
        // if every host that reads and writes it agrees on byte order. Every Azure Functions
        // host this app runs on is little-endian; if that ever stops being true the cache must
        // be given an explicit byte order, not silently read backwards.
        if (!BitConverter.IsLittleEndian)
            throw new PlatformNotSupportedException("VectorCache stores little-endian float32; this host is big-endian.");
    }

    public VectorCache(BlobContainerClient container)
    {
        _container = container;
    }

    public async Task<CachedVector?> TryGetAsync(string contentHash, CancellationToken ct = default)
    {
        var blob = _container.GetBlobClient(BlobName(contentHash));

        Response<BlobDownloadResult> download;
        var started = Stopwatch.GetTimestamp();
        try
        {
            download = await blob.DownloadContentAsync(ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            RecordOp("get_miss", started);
            return null;
        }
        RecordOp("get_hit", started);

        var content = download.Value.Content.ToMemory();
        var decodeStarted = Stopwatch.GetTimestamp();

        // A blob that is not a whole number of floats, or is empty, is corrupt or partially
        // written - a miss, so the caller re-embeds and overwrites it rather than failing the run
        // over one bad cache blob. Same policy the JSON format had for a JsonException.
        if (content.Length == 0 || content.Length % BytesPerFloat != 0)
            return null;

        var vector = MemoryMarshal.Cast<byte, float>(content.Span).ToArray();
        var decodeTicks = Stopwatch.GetTimestamp() - decodeStarted;
        return new CachedVector(vector, content.Length, decodeTicks);
    }

    // No container create here. Until 2026-09-16 every call began with CreateIfNotExistsAsync -
    // a second HTTP round trip per write, ~40 ms each at the measured blob latency (D196 §3), so
    // 255 extra operations on a warm run and 3,703 on a cold one for a container Terraform has
    // owned since infra/storage.tf declared it. The once-per-run check is AssertContainerExistsAsync
    // below, called by VectorCacheGateway.WriteFreshAsync (D197 action 1c).
    public async Task<long> SetAsync(string contentHash, float[] vector, CancellationToken ct = default)
    {
        var bytes = MemoryMarshal.AsBytes(vector.AsSpan()).ToArray();
        using var ms = new MemoryStream(bytes);
        var started = Stopwatch.GetTimestamp();
        await _container.GetBlobClient(BlobName(contentHash)).UploadAsync(ms, overwrite: true, cancellationToken: ct);
        RecordOp("put", started);
        return bytes.Length;
    }

    // Existence check, never a create - the same policy and the same exception as
    // IBlobStore.AssertContainerExistsAsync, for the same reason: a silent auto-create on a
    // name mismatch is how pipeline-artifacts itself once ended up with an unmanaged twin
    // (see IBlobStore). Once per run, not per write.
    public async Task AssertContainerExistsAsync(CancellationToken ct = default)
    {
        if (!await _container.ExistsAsync(ct))
            throw new ContainerNotDeclaredException(_container.Name);
    }

    // Two phases, two clocks (2026-09-18, D203 M5a). The listing is paged by the SDK, so the
    // list clock runs across the whole enumeration and the deletes are done AFTER it rather
    // than interleaved - otherwise a page fetch and a delete would land in the same bracket
    // and neither number would mean anything.
    //
    // Deletes run MaxCacheParallelism-wide (2026-09-18, D203 O3). Until then they were one round
    // trip at a time: 306 orphans × 14.8 ms mean = 4.5-5.1 s on every force run and every churn
    // day, 180 s on the one run that had a backlog (260917/4, 11,038). Runs 260918/1 and /4
    // measured DELETE as the tightest op the cache makes - p50 11.4 ms, p95 33.6, no tail - so
    // the whole gain is the fan-out and there is nothing else to chase here. The same constant
    // as the read and write passes, on purpose: D197 §5 measured the store's contention point
    // with it, and a second knob would drift from that evidence. Not the Blob Batch API: it is a
    // separate package this project does not carry, and at 306 deletes the fan-out reaches the
    // same sub-second result. DeleteMs stays wall-clock; DeleteLatency stays per call.
    //
    // Legacy sweep (D203 O2): a `.json` entry is the JSON-era format, unreadable by TryGetAsync
    // and never written again, so it is deleted whether or not its hash is live - its vector is
    // re-embedded on the first run after the format change and rewritten as `.f32`. One-off:
    // ~3,975 deletes on that run, then zero. Counted in LegacyDeleted, not in Deleted, so the
    // report's ChunksEvicted keeps meaning "orphans".
    public async Task<VectorCacheEviction> EvictOrphanedAsync(IReadOnlySet<string> liveHashes, CancellationToken ct = default)
    {
        var orphans = new List<string>();
        var legacy  = new List<string>();
        var sizes   = new List<long>();
        var listed  = 0;

        var listClock = Stopwatch.StartNew();
        var listStarted = Stopwatch.GetTimestamp();
        await foreach (var blobItem in _container.GetBlobsAsync(BlobTraits.None, BlobStates.None, $"{Prefix}/", ct))
        {
            listed++;
            var name = blobItem.Name;

            if (name.EndsWith(LegacySuffix, StringComparison.Ordinal))
            {
                legacy.Add(name);
                continue;
            }
            if (!name.EndsWith(Suffix, StringComparison.Ordinal))
                continue; // not ours to judge - neither format this class has ever written

            if (blobItem.Properties?.ContentLength is { } length) sizes.Add(length);

            var hash = name[(Prefix.Length + 1)..^Suffix.Length];
            if (!liveHashes.Contains(hash)) orphans.Add(name);
        }
        listClock.Stop();
        RecordOp("list", listStarted);

        var deleteClock = Stopwatch.StartNew();
        var deleteMs    = new ConcurrentBag<double>();
        await Parallel.ForEachAsync(
            orphans.Concat(legacy),
            new ParallelOptions { MaxDegreeOfParallelism = VectorCacheGateway.MaxCacheParallelism, CancellationToken = ct },
            async (name, token) =>
            {
                var started = Stopwatch.GetTimestamp();
                await _container.GetBlobClient(name).DeleteIfExistsAsync(cancellationToken: token);
                deleteMs.Add(RecordOp("delete", started));
            });
        deleteClock.Stop();

        long? total = null, p50 = null;
        if (sizes.Count > 0)
        {
            sizes.Sort();
            total = sizes.Sum();
            p50   = sizes[sizes.Count / 2];
        }

        return new VectorCacheEviction(
            Listed:         listed,
            Deleted:        orphans.Count,
            ListMs:         listClock.ElapsedMilliseconds,
            DeleteMs:       deleteClock.ElapsedMilliseconds,
            BlobBytesTotal: total,
            BlobBytesP50:   p50,
            DeleteLatency:  LatencySummary.From(deleteMs),
            LegacyDeleted:  legacy.Count);
    }

    private static string BlobName(string contentHash) => $"{Prefix}/{contentHash}{Suffix}";

    // Records the op on the histogram and returns the same milliseconds, so a caller that also
    // summarises for the report (D203 §6c) uses one reading for both.
    private static double RecordOp(string op, long startedTimestamp)
    {
        var ms = Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;
        Instrumentation.VectorCacheOpMs.Record(ms, new KeyValuePair<string, object?>("op", op));
        return ms;
    }
}
