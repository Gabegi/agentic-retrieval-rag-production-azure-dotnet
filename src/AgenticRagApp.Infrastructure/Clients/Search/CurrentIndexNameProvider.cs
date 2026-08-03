using Azure.Storage.Blobs;
using AgenticRagApp.Infrastructure.Clients.Blob;
using AgenticRagApp.Infrastructure.Configuration;

namespace AgenticRagApp.Infrastructure.Clients.Search;

public class CurrentIndexNameProvider : ICurrentIndexNameProvider
{
    // Not source-scoped like RunReportWriter's "_last-stats-{source}" baselines - PDF and CSV
    // chunks share the one index (see IndexService's own comment), so there is exactly one
    // pointer for the whole app, not one per doc-type pipeline.
    private const string PointerPath = "indexing/_current-index-name.json";

    private readonly IBlobStore          _blobStore;
    private readonly BlobContainerClient _container;
    private readonly IndexerConfig       _config;

    public CurrentIndexNameProvider(IBlobStore blobStore, BlobContainerClient container, IndexerConfig config)
    {
        _blobStore = blobStore;
        _container = container;
        _config    = config;
    }

    public async Task<string> GetCurrentIndexNameAsync(CancellationToken ct = default)
    {
        try
        {
            var (pointer, _) = await _blobStore.TryReadJsonWithETagAsync<IndexNamePointer>(_container, PointerPath, ct);
            return string.IsNullOrWhiteSpace(pointer?.IndexName) ? _config.SearchIndexName : pointer.IndexName;
        }
        catch
        {
            // Missing/corrupt pointer should never block indexing or querying - fall back to
            // the configured base name, same as a pre-generations deployment would use.
            return _config.SearchIndexName;
        }
    }

    public async Task SetCurrentIndexNameAsync(string indexName, CancellationToken ct = default)
    {
        await _blobStore.EnsureContainerExistsAsync(_container, ct);
        await _blobStore.UploadJsonAsync(_container, PointerPath, new IndexNamePointer(indexName), ct);
    }

    private record IndexNamePointer(string IndexName);
}
