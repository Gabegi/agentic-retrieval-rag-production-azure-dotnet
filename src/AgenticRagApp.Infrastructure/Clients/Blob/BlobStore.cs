using System.IO.Pipelines;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;

namespace AgenticRagApp.Infrastructure.Clients.Blob;

public class BlobStore : IBlobStore
{
    private readonly ILogger<BlobStore> _logger;

    public BlobStore(ILogger<BlobStore> logger) => _logger = logger;

    public async Task AssertContainerExistsAsync(BlobContainerClient container, CancellationToken ct = default)
    {
        if (!await container.ExistsAsync(ct))
            throw new ContainerNotDeclaredException(container.Name);
    }

    public async Task<byte[]> DownloadBytesAsync(BlobContainerClient container, string blobName, CancellationToken ct = default)
    {
        var download = await container.GetBlobClient(blobName).DownloadContentAsync(ct);
        return download.Value.Content.ToArray();
    }

    public async Task<Stream> OpenReadAsync(BlobContainerClient container, string blobName, CancellationToken ct = default) =>
        await container.GetBlobClient(blobName).OpenReadAsync(cancellationToken: ct);

    public async Task<bool> ExistsAsync(BlobContainerClient container, string blobName, CancellationToken ct = default) =>
        await container.GetBlobClient(blobName).ExistsAsync(ct);

    public async Task UploadAsync(BlobContainerClient container, string blobName, BinaryData content, bool overwrite, CancellationToken ct = default) =>
        await container.GetBlobClient(blobName).UploadAsync(content, overwrite: overwrite, cancellationToken: ct);

    public async Task<bool> DeleteIfExistsAsync(BlobContainerClient container, string blobName, CancellationToken ct = default) =>
        await container.GetBlobClient(blobName).DeleteIfExistsAsync(cancellationToken: ct);

    public async Task<IReadOnlyList<(string Name, DateTimeOffset? LastModified, long? ContentLength, IReadOnlyDictionary<string, string> Metadata)>> ListBlobsAsync(
        BlobContainerClient container, string? prefix = null, CancellationToken ct = default)
    {
        var result = new List<(string, DateTimeOffset?, long?, IReadOnlyDictionary<string, string>)>();
        await foreach (var item in container.GetBlobsAsync(BlobTraits.Metadata, BlobStates.None, prefix, ct))
            // Azure Blob Storage treats metadata key names as case-insensitive (a manual
            // upload setting "Zenya_Document_Id" is the same key as "zenya_document_id" to
            // Azure), but the SDK hands back item.Metadata as an ordinal-comparer dictionary -
            // wrapped here so a differently-cased key doesn't silently miss every lookup
            // (ZenyaMetadata.FromBlobMetadata) and read as "not set".
            result.Add((item.Name, item.Properties.LastModified, item.Properties.ContentLength,
                (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(
                    item.Metadata ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase)));
        return result;
    }

    public async Task<T> DownloadJsonAsync<T>(BlobContainerClient container, string blobName, CancellationToken ct = default)
    {
        var download = await container.GetBlobClient(blobName).DownloadContentAsync(ct);
        return download.Value.Content.ToObjectFromJson<T>()!;
    }

    public async Task UploadJsonAsync<T>(
        BlobContainerClient container, string blobName, T value, JsonSerializerOptions? options = null, CancellationToken ct = default)
    {
        // A System.IO.Pipelines.Pipe connects a writer and a reader running concurrently, with
        // a small bounded internal buffer - the writer blocks (backpressure) once the reader
        // falls behind, rather than the writer racing ahead and accumulating the whole payload
        // in memory the way a MemoryStream would. JsonSerializer writes progressively into one
        // end while BlobClient.UploadAsync reads from the other and stages blocks to the
        // service; at no point does either side hold the complete serialized payload at once.
        //
        // Both sides run as one Task each so a slow/large upload doesn't block the CPU-bound
        // serialize step, and vice versa.
        var pipe = new Pipe();

        var writeTask = WriteAsync();
        var uploadTask = container.GetBlobClient(blobName).UploadAsync(pipe.Reader.AsStream(), overwrite: true, ct);
        await Task.WhenAll(writeTask, uploadTask);

        async Task WriteAsync()
        {
            Exception? failure = null;
            try
            {
                await JsonSerializer.SerializeAsync(pipe.Writer.AsStream(), value, options, ct);
            }
            catch (Exception ex)
            {
                // Propagated to the reader side below, so a serialization failure faults the
                // upload task too instead of leaving it waiting forever on data that will never
                // arrive.
                failure = ex;
                throw;
            }
            finally
            {
                await pipe.Writer.CompleteAsync(failure);
            }
        }
    }

    public async Task<(T? Value, ETag? ETag)> TryReadJsonWithETagAsync<T>(
        BlobContainerClient container, string blobName, CancellationToken ct = default)
    {
        try
        {
            var download = await container.GetBlobClient(blobName).DownloadContentAsync(ct);
            var value    = download.Value.Content.ToObjectFromJson<T>();
            return (value, download.Value.Details.ETag);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // No previous state blob — first run, or it was removed. Attempting the
            // download directly (rather than ExistsAsync then DownloadContentAsync) avoids
            // a second round-trip and the race between the two calls.
            return (default, null);
        }
        catch (JsonException ex)
        {
            // A corrupt/partially-written state blob shouldn't brick the caller — treat it
            // the same as the blob not existing (no usable baseline).
            _logger.LogWarning(ex,
                "State blob '{Blob}' contains invalid JSON — treating as no previous baseline.", blobName);
            return (default, null);
        }
    }

    public async Task<bool> SaveJsonWithETagAsync<T>(
        BlobContainerClient container, string blobName, T value, ETag? previousETag, CancellationToken ct = default)
    {
        var json       = JsonSerializer.Serialize(value);
        var conditions = previousETag is { } tag
            ? new BlobRequestConditions { IfMatch = tag }
            : new BlobRequestConditions { IfNoneMatch = ETag.All };

        try
        {
            await container.GetBlobClient(blobName)
                .UploadAsync(BinaryData.FromString(json), new BlobUploadOptions { Conditions = conditions }, ct);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status is 412 or 409)
        {
            // Lost a race with another concurrent writer — 412 is the IfMatch/IfNoneMatch
            // precondition failure; 409 (BlobAlreadyExists) is what the first-write
            // IfNoneMatch: * path gets instead when the blob was created concurrently.
            // Not worth failing the caller's otherwise-successful run over.
            _logger.LogWarning(
                "State blob '{Blob}' was updated concurrently — this write was not saved.", blobName);
            return false;
        }
    }
}
