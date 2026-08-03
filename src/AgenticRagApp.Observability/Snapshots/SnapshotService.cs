using AgenticRagApp.Common.Models;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;
using AgenticRagApp.Infrastructure.Clients.Blob;

namespace AgenticRagApp.Observability.Reports;

public class SnapshotService : ISnapshotService
{
    private const string FileName = "full-index.json";

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

    public async Task<IReadOnlySet<string>> UpdateAsync<T>(
        string source, IReadOnlyList<T> newChunks, IReadOnlyList<string> staleDocumentIds, string instanceId, DateTimeOffset startedAt, CancellationToken ct = default)
        where T : ISnapshotSource
    {
        var prefix = $"snapshots/{source}/";

        // Newest-first, so [0] is "the previous snapshot" to merge from, and everything from
        // MaxRetainedSnapshots-1 onward is what gets pruned once this run's new one is added.
        var existing = await ListSnapshotBlobsAsync(prefix, ct);

        var previous = existing.Count > 0
            ? await ReadSnapshotAsync(existing[0].Path, ct)
            : [];

        // Drop old entries for any document this run touched (updated or removed), then add
        // this run's fresh chunks. A document untouched this run keeps its previous entry
        // unchanged - that's how the snapshot accumulates into a full-corpus picture over time.
        var staleSet = new HashSet<string>(staleDocumentIds, StringComparer.OrdinalIgnoreCase);
        var merged = previous
            .Where(c => !staleSet.Contains(c.DocumentId))
            .Concat(newChunks.Select(SnapshotChunk.From))
            .ToList();

        var path = $"{prefix}{startedAt:yyyy/MM/dd}/{instanceId}/{FileName}";
        await _blobStore.EnsureContainerExistsAsync(_container, ct);
        await _blobStore.UploadJsonAsync(_container, path, merged, ct);
        _logger.LogInformation("Snapshot written — source '{Source}', {Count} chunks → {Path}", source, merged.Count, path);

        var prunedCount = await PruneAsync(existing, ct);
        if (prunedCount > 0)
            _logger.LogInformation("Snapshot pruning — source '{Source}', {Count} older snapshot(s) deleted", source, prunedCount);

        return merged.Select(c => c.ContentHash).ToHashSet();
    }

    public async Task<(IReadOnlyList<SnapshotChunk> Chunks, string? InstanceId)> ReadLatestAsync(
        string source, CancellationToken ct = default)
    {
        var prefix   = $"snapshots/{source}/";
        var existing = await ListSnapshotBlobsAsync(prefix, ct);
        if (existing.Count == 0) return ([], null);

        var latest = existing[0];
        var chunks = await ReadSnapshotAsync(latest.Path, ct);

        // Path is {prefix}{yyyy}/{MM}/{dd}/{instanceId}/{FileName} — instanceId is the last
        // segment before the file name, not everything after the prefix.
        var dirs       = latest.Path[prefix.Length..^($"/{FileName}".Length)];
        var instanceId = dirs[(dirs.LastIndexOf('/') + 1)..];
        return (chunks, instanceId);
    }

    private async Task<List<(string Path, DateTimeOffset LastModified)>> ListSnapshotBlobsAsync(string prefix, CancellationToken ct)
    {
        var blobs = await _blobStore.ListBlobsAsync(_container, prefix, ct);

        return blobs
            .Where(b => b.Name.EndsWith($"/{FileName}", StringComparison.Ordinal))
            .Select(b => (b.Name, b.LastModified ?? DateTimeOffset.MinValue))
            .OrderByDescending(b => b.Item2)
            .ToList();
    }

    // Keeps the newest (MaxRetainedSnapshots - 1) of the pre-existing snapshots - one slot is
    // already spoken for by the new snapshot UpdateAsync just wrote - and deletes the rest.
    private async Task<int> PruneAsync(List<(string Path, DateTimeOffset LastModified)> existing, CancellationToken ct)
    {
        var toDelete = existing.Skip(MaxRetainedSnapshots - 1).ToList();
        foreach (var (path, _) in toDelete)
            await _blobStore.DeleteIfExistsAsync(_container, path, ct);

        return toDelete.Count;
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
