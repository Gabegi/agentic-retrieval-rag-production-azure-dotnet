using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;

namespace AgenticRagApp.Infrastructure.Clients.Blob;

// Generic wrapper around BlobContainerClient — every blob read/write in the app goes
// through this, regardless of which container or which pipeline. Callers decide which
// container to pass and how to orchestrate calls (parallel, sequential, one file vs many);
// this only ever does the mechanical Azure call.
public interface IBlobStore
{
    // Fails loudly (BlobContainerNotFoundException) if the container does not exist, rather than
    // creating it. Terraform owns every container this app writes to (see infra/storage.tf) -
    // silently auto-creating one on a name mismatch is exactly how pipeline-reports and
    // pipeline-artifacts each ended up with a managed container sitting empty while writes went
    // to an unmanaged, auto-created one of a slightly different name. That drift went unnoticed
    // for weeks because the old EnsureContainerExistsAsync never failed.
    //
    // Call this once per container per caller, not per write - it's an existence check, not a
    // no-op-safe idempotent create, so callers should not pay a network round trip on every blob
    // operation for it.
    Task AssertContainerExistsAsync(BlobContainerClient container, CancellationToken ct = default);

    Task<byte[]> DownloadBytesAsync(BlobContainerClient container, string blobName, CancellationToken ct = default);

    Task<Stream> OpenReadAsync(BlobContainerClient container, string blobName, CancellationToken ct = default);

    Task<bool> ExistsAsync(BlobContainerClient container, string blobName, CancellationToken ct = default);

    Task UploadAsync(BlobContainerClient container, string blobName, BinaryData content, bool overwrite, CancellationToken ct = default);

    Task<bool> DeleteIfExistsAsync(BlobContainerClient container, string blobName, CancellationToken ct = default);

    // Cheap listing — blob name, storage LastModified, content length, and custom metadata
    // only, no content download. prefix narrows the listing server-side (e.g. "snapshots/pdf/")
    // when only one folder within the container is of interest; null lists the whole container.
    // Metadata is whatever custom key/value pairs were set on the blob (e.g. by whoever
    // uploaded it) - empty dictionary, never null, when none were set.
    Task<IReadOnlyList<(string Name, DateTimeOffset? LastModified, long? ContentLength, IReadOnlyDictionary<string, string> Metadata)>> ListBlobsAsync(
        BlobContainerClient container, string? prefix = null, CancellationToken ct = default);

    Task<T> DownloadJsonAsync<T>(BlobContainerClient container, string blobName, CancellationToken ct = default);

    // Streams the serialized value directly into the blob upload - never materializes the
    // whole payload as an intermediate string or byte[] first. A naive serialize-then-upload
    // (JsonSerializer.Serialize -> string -> BinaryData -> byte[]) holds up to ~3 full copies
    // of the payload in memory at once (the object graph, the UTF-16 string, and the UTF-8
    // byte array), on top of whatever else is resident in the process. That is exactly what
    // caused a production OutOfMemoryException writing a ~2,700-chunk pipeline-artifacts
    // archive on an EP1 plan's 3.5GB ceiling (2026-08-07) - see PipelineArtifactWriter.
    //
    // options is optional so callers needing custom converters (e.g. JsonStringEnumConverter,
    // for enums that should serialize as names, not numbers) aren't forced onto the default.
    Task UploadJsonAsync<T>(BlobContainerClient container, string blobName, T value, JsonSerializerOptions? options = null, CancellationToken ct = default);

    // Returns (default, null) if the blob doesn't exist yet — "no previous baseline" is a
    // normal, expected outcome for state blobs, not an error.
    Task<(T? Value, ETag? ETag)> TryReadJsonWithETagAsync<T>(BlobContainerClient container, string blobName, CancellationToken ct = default);

    // Optimistic-concurrency write: matches previousETag if given, otherwise requires the
    // blob not exist yet (first write wins). Returns false (and logs a warning) instead of
    // throwing if another writer won the race — losing this race isn't worth failing an
    // otherwise-successful caller over.
    Task<bool> SaveJsonWithETagAsync<T>(BlobContainerClient container, string blobName, T value, ETag? previousETag, CancellationToken ct = default);
}
