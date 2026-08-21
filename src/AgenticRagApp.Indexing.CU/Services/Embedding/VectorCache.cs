using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace AgenticRagApp.Indexing.CU.Services;

// One blob per content hash under pipeline-artifacts/vector-cache/ - not one shared file,
// so a read never races a concurrent write from another chunk and eviction (Stage 4) can
// delete individual orphaned entries without touching the rest.
public class VectorCache : IVectorCache
{
    private const string Prefix = "vector-cache";
    private readonly BlobContainerClient _container;

    public VectorCache(BlobContainerClient container)
    {
        _container = container;
    }

    public async Task<float[]?> TryGetAsync(string contentHash, CancellationToken ct = default)
    {
        var blob = _container.GetBlobClient($"{Prefix}/{contentHash}.json");

        try
        {
            var download = await blob.DownloadContentAsync(ct);
            return JsonSerializer.Deserialize<float[]>(download.Value.Content.ToMemory().Span);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
        catch (JsonException)
        {
            // Corrupt/partially-written entry - treat as a miss and let the caller re-embed
            // and overwrite it, rather than failing the whole run over one bad cache blob.
            return null;
        }
    }

    public async Task SetAsync(string contentHash, float[] vector, CancellationToken ct = default)
    {
        await _container.CreateIfNotExistsAsync(cancellationToken: ct);
        var json = JsonSerializer.SerializeToUtf8Bytes(vector);
        using var ms = new MemoryStream(json);
        await _container.GetBlobClient($"{Prefix}/{contentHash}.json").UploadAsync(ms, overwrite: true, cancellationToken: ct);
    }

    public async Task<int> EvictOrphanedAsync(IReadOnlySet<string> liveHashes, CancellationToken ct = default)
    {
        var deleted = 0;

        await foreach (var blobItem in _container.GetBlobsAsync(BlobTraits.None, BlobStates.None, $"{Prefix}/", ct))
        {
            var hash = blobItem.Name[(Prefix.Length + 1)..^".json".Length];
            if (liveHashes.Contains(hash)) continue;

            await _container.GetBlobClient(blobItem.Name).DeleteIfExistsAsync(cancellationToken: ct);
            deleted++;
        }

        return deleted;
    }
}
