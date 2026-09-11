using AgenticRagApp.Infrastructure.Clients.Blob;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace AgenticRagApp.Infrastructure.Clients.Zenya.Sync;

// IZenyaDocumentStore over one BlobContainerClient. Listing, existence check and delete go
// through the app's IBlobStore like every other blob access; the upload does not, because
// IBlobStore.UploadAsync has no metadata/content-type parameters and the metadata IS the point
// of this container (ZenyaBlobLayout).
public sealed class BlobZenyaDocumentStore : IZenyaDocumentStore
{
    private readonly BlobContainerClient _container;
    private readonly IBlobStore _blobs;

    public BlobZenyaDocumentStore(BlobContainerClient container, IBlobStore blobs)
    {
        _container = container;
        _blobs = blobs;
    }

    public Task EnsureReadyAsync(CancellationToken ct = default) =>
        _blobs.AssertContainerExistsAsync(_container, ct);

    public async Task<IReadOnlyList<StoredZenyaBlob>> ListAsync(CancellationToken ct = default)
    {
        var listed = await _blobs.ListBlobsAsync(_container, prefix: null, ct);
        return listed.Select(b => new StoredZenyaBlob(b.Name, b.Metadata)).ToList();
    }

    public Task UploadAsync(string blobName, Stream content, string? contentType, IReadOnlyDictionary<string, string> metadata, CancellationToken ct = default) =>
        _container.GetBlobClient(blobName).UploadAsync(content, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = contentType ?? "application/octet-stream" },
            Metadata    = new Dictionary<string, string>(metadata),
        }, ct);

    public Task DeleteAsync(string blobName, CancellationToken ct = default) =>
        _blobs.DeleteIfExistsAsync(_container, blobName, ct);
}
