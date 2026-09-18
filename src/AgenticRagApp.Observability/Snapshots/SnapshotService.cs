using AgenticRagApp.Common.Models;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;
using AgenticRagApp.Infrastructure.Clients.Blob;

namespace AgenticRagApp.Observability.Reports;

public class SnapshotService : ISnapshotService
{
    // Keep the 3 most recent snapshots per source - an explicit exception to the archive's
    // otherwise "keep forever" retention, scoped only to this path.
    private const int MaxRetainedSnapshots = 3;

    private readonly IBlobStore              _blobStore;
    private readonly BlobContainerClient      _container;
    private readonly ILogger<SnapshotService> _logger;

    public SnapshotService(IBlobStore blobStore, BlobContainerClient container, ILogger<SnapshotService> logger)
    {
        _blobStore = blobStore;
        _container = container;
        _logger    = logger;
    }

    // Pointer entry, not just a bare path: parsing an instance ID back out of a filename is
    // brittle (Durable instance IDs are GUIDs, which themselves contain '-', the same
    // separator ReportPath uses), so it's carried alongside the path instead of re-derived.
    // Internal (not private) + AssemblyInfo.cs's InternalsVisibleTo so tests can set up
    // IBlobStore.TryReadJsonWithETagAsync<SnapshotPointer> directly.
    internal sealed record SnapshotPointerEntry(string Path, string InstanceId);
    internal sealed record SnapshotPointer(IReadOnlyList<SnapshotPointerEntry> Entries);

    private static string PointerPath(string source) => $"_latest-snapshot-{source}.json";

    public async Task<SnapshotLiveSet> UpdateAsync<T>(
        string source, IReadOnlyList<T> newChunks, IReadOnlyList<string> staleDocumentIds, IReadOnlyList<string> processedDocumentIds, string instanceId, DateTimeOffset startedAt, CancellationToken ct = default)
        where T : ISnapshotSource
    {
        await _blobStore.AssertContainerExistsAsync(_container, ct);

        var (pointer, etag) = await _blobStore.TryReadJsonWithETagAsync<SnapshotPointer>(_container, PointerPath(source), ct);
        var existingEntries  = pointer?.Entries ?? [];

        var previous = existingEntries.Count > 0
            ? await ReadSnapshotAsync(existingEntries[0].Path, ct)
            : [];

        // Drop old entries for any document this run touched, then add this run's fresh chunks.
        // A document untouched this run keeps its previous entry unchanged - that is how the
        // snapshot accumulates into a full-corpus picture over time.
        //
        // "Touched" is the union of two sets, and using only the first was D200 F1 (fixed
        // 2026-09-17). staleDocumentIds covers documents the diff marked updated or removed -
        // but on a run that recreates the index first, the diff sees an empty index, every
        // document reads as NEW, and that list is empty. processedDocumentIds covers what was
        // actually re-extracted, which is the set whose previous rows are genuinely superseded.
        //
        // Union rather than replacement: a REMOVED document is stale but not processed (it no
        // longer exists to extract), and its rows must still go.
        var dropSet = new HashSet<string>(staleDocumentIds, StringComparer.OrdinalIgnoreCase);

        // "Processed" means SUCCESSFULLY CHUNKED, not merely attempted - so a processed document
        // only joins the drop set if it actually produced chunks to replace its old rows with.
        //
        // The failure modes are not symmetric. A document that fails somewhere between extraction
        // and chunking is still in processedDocumentIds; dropping its rows with nothing to re-add
        // would delete it from the snapshot, and the snapshot is what RestoreService rebuilds the
        // index from - so a transient failure would silently cost a document on the next restore.
        // The other direction merely keeps superseded rows for a document that legitimately
        // produced none, which is the leak this whole change is about and is recoverable on any
        // later run. Retain the leak, never the data loss.
        var producedChunks = newChunks
            .Select(c => c.DocumentId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        dropSet.UnionWith(processedDocumentIds.Where(producedChunks.Contains));

        var merged = previous
            .Where(c => !dropSet.Contains(c.DocumentId))
            .Concat(newChunks.Select(SnapshotChunk.From))
            .ToList();

        // Loud, because silence here is what let 93.4% of the rows accumulate unnoticed: a
        // merged set far larger than what came in means the drop set is not doing its job.
        _logger.LogInformation(
            "Snapshot merge — source '{Source}': {Previous} previous row(s), {Dropped} document(s) in the drop set, {New} new row(s) → {Merged} live",
            source, previous.Count, dropSet.Count, newChunks.Count, merged.Count);

        var path = ReportPath.Build(startedAt, $"snapshot-{source}", instanceId);
        // Streamed - by far the largest payload in the system (the whole corpus's snapshot,
        // growing unboundedly over time) going through the double-buffering write path this
        // OOM'd on elsewhere in production. See IBlobStore.UploadJsonAsync.
        await _blobStore.UploadJsonAsync(_container, path, merged, ct: ct);
        _logger.LogInformation("Snapshot written — source '{Source}', {Count} chunks → {Path}", source, merged.Count, path);

        // Newest-first, one slot already spoken for by the new snapshot just written - keep
        // (MaxRetainedSnapshots - 1) of the pre-existing entries and prune the rest.
        var retained = existingEntries.Take(MaxRetainedSnapshots - 1).ToList();
        var pruned   = existingEntries.Skip(MaxRetainedSnapshots - 1).ToList();

        foreach (var entry in pruned)
            await _blobStore.DeleteIfExistsAsync(_container, entry.Path, ct);
        if (pruned.Count > 0)
            _logger.LogInformation("Snapshot pruning — source '{Source}', {Count} older snapshot(s) deleted", source, pruned.Count);

        var newPointer = new SnapshotPointer([new SnapshotPointerEntry(path, instanceId), .. retained]);
        var saved = await _blobStore.SaveJsonWithETagAsync(_container, PointerPath(source), newPointer, etag, ct);
        if (!saved)
            _logger.LogWarning("Lost the race updating the snapshot pointer for source '{Source}' — this run's snapshot at '{Path}' was still written, just not pointed to.", source, path);

        // Document ids are compared case-insensitively, matching staleSet above and the rest of
        // the pipeline's SourceId handling - a case-only difference must never read as "this
        // document is gone", since the caller turns that into a delete.
        return new SnapshotLiveSet(
            merged.Select(c => c.ContentHash).ToHashSet(),
            merged.Select(c => c.DocumentId).ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    public async Task<(IReadOnlyList<SnapshotChunk> Chunks, string? InstanceId)> ReadLatestAsync(
        string source, CancellationToken ct = default)
    {
        var (pointer, _) = await _blobStore.TryReadJsonWithETagAsync<SnapshotPointer>(_container, PointerPath(source), ct);
        var latest = pointer?.Entries.Count > 0 ? pointer.Entries[0] : null;
        if (latest is null) return ([], null);

        var chunks = await ReadSnapshotAsync(latest.Path, ct);
        return (chunks, latest.InstanceId);
    }

    private async Task<List<SnapshotChunk>> ReadSnapshotAsync(string path, CancellationToken ct)
    {
        try
        {
            return await _blobStore.DownloadJsonAsync<List<SnapshotChunk>>(_container, path, ct) ?? [];
        }
        catch (Exception ex)
        {
            // Missing/corrupt previous snapshot shouldn't block this run - starts the merge
            // from empty, same as the very first run ever. Self-corrects over subsequent runs.
            _logger.LogWarning(ex, "Failed to read previous snapshot '{Path}' — starting merge from empty.", path);
            return [];
        }
    }
}
