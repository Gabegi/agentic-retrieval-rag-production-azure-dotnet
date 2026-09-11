namespace AgenticRagApp.Infrastructure.Clients.Zenya.Sync;

// The target container as the sync sees it: a listing with metadata, an upload with metadata,
// a delete. Kept this narrow so ZenyaSyncService is testable against an in-memory store and so
// the only Azure-specific code is BlobZenyaDocumentStore.
public interface IZenyaDocumentStore
{
    // Fails if the container does not exist (terraform owns it - infra/storage.tf); never creates.
    Task EnsureReadyAsync(CancellationToken ct = default);

    // Every blob in the container with its custom metadata. The sync keys on
    // ZenyaBlobLayout.DocumentIdKey; blobs without it are reported, never deleted.
    Task<IReadOnlyList<StoredZenyaBlob>> ListAsync(CancellationToken ct = default);

    // Creates or overwrites. contentType null = application/octet-stream.
    Task UploadAsync(string blobName, Stream content, string? contentType, IReadOnlyDictionary<string, string> metadata, CancellationToken ct = default);

    Task DeleteAsync(string blobName, CancellationToken ct = default);
}

public sealed record StoredZenyaBlob(string Name, IReadOnlyDictionary<string, string> Metadata)
{
    public string? DocumentId => Metadata.GetValueOrDefault(ZenyaBlobLayout.DocumentIdKey);

    public int? Version =>
        int.TryParse(Metadata.GetValueOrDefault(ZenyaBlobLayout.VersionKey), out var v) ? v : null;
}
