using System.Text;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace AgenticRagApp.Infrastructure.Clients.DocumentIdentity;

// One blob per SourceId under pipeline-artifacts/document-identity/ - same "one blob per
// key, not one shared file" shape as VectorCache, for the same reason (a read never races a
// concurrent write for a different document). SourceId is a blob name (can contain '/' and
// spaces - see PdfExtractionDocument.SourceId), so it's base64-encoded into the blob key
// rather than used as a path segment directly.
//
// Lives in Infrastructure rather than Indexing.Pdf because it is a storage client, and this
// project's rule is that raw SDK clients (here BlobContainerClient) are only ever held behind
// a wrapper defined here - see the registration comment in ServiceCollectionExtensions. Its
// consumer is DocumentIdentityResolver in Indexing.Pdf, which now depends on the interface
// rather than owning the implementation.
public class DocumentIdentityStore : IDocumentIdentityStore
{
    private const string Prefix = "document-identity";
    private readonly BlobContainerClient _container;

    public DocumentIdentityStore(BlobContainerClient container)
    {
        _container = container;
    }

    public async Task<IReadOnlyList<DocumentIdentityRecord>> GetAllAsync(CancellationToken ct = default)
    {
        var records = new List<DocumentIdentityRecord>();

        await foreach (var blobItem in _container.GetBlobsAsync(BlobTraits.None, BlobStates.None, $"{Prefix}/", ct))
        {
            try
            {
                var download = await _container.GetBlobClient(blobItem.Name).DownloadContentAsync(ct);
                var record   = JsonSerializer.Deserialize<DocumentIdentityRecord>(download.Value.Content.ToMemory().Span);
                if (record is not null)
                    records.Add(record);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Deleted between listing and download - skip, not a failure.
            }
            catch (JsonException)
            {
                // Corrupt/partially-written entry - treat as absent rather than failing the
                // whole clustering pass over one bad blob; the next SetAsync for that
                // SourceId overwrites it.
            }
        }

        return records;
    }

    public async Task SetAsync(DocumentIdentityRecord record, CancellationToken ct = default)
    {
        await _container.CreateIfNotExistsAsync(cancellationToken: ct);
        var json = JsonSerializer.SerializeToUtf8Bytes(record);
        using var ms = new MemoryStream(json);
        await _container.GetBlobClient($"{Prefix}/{SafeKey(record.SourceId)}.json").UploadAsync(ms, overwrite: true, cancellationToken: ct);
    }

    // Reads each record to get its SourceId rather than decoding it back out of the blob name:
    // SafeKey's '+'/'/' substitution is not reversible without knowing which characters were
    // replaced, and a wrong guess here deletes a live document's identity. The listing is the
    // whole store (dozens of small blobs at this corpus's scale), and GetAllAsync already reads
    // all of them on every run.
    public async Task<int> EvictOrphanedAsync(IReadOnlySet<string> liveSourceIds, CancellationToken ct = default)
    {
        // An empty live set means the snapshot had nothing in it - a first run, or a snapshot
        // that has not been built yet. Treating that as "everything is orphaned" would wipe the
        // store, so it is explicitly a no-op.
        if (liveSourceIds.Count == 0) return 0;

        var deleted = 0;

        await foreach (var blobItem in _container.GetBlobsAsync(BlobTraits.None, BlobStates.None, $"{Prefix}/", ct))
        {
            DocumentIdentityRecord? record;
            try
            {
                var download = await _container.GetBlobClient(blobItem.Name).DownloadContentAsync(ct);
                record = JsonSerializer.Deserialize<DocumentIdentityRecord>(download.Value.Content.ToMemory().Span);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                continue;
            }
            catch (JsonException)
            {
                // Unreadable, so its SourceId is unknown and it cannot be matched against the
                // live set. Left alone rather than deleted: GetAllAsync already skips it, so it
                // is inert, and deleting on "we couldn't parse it" is the wrong default for the
                // only durable copy of a document's identity.
                continue;
            }

            if (record is null || liveSourceIds.Contains(record.SourceId)) continue;

            await _container.GetBlobClient(blobItem.Name).DeleteIfExistsAsync(cancellationToken: ct);
            deleted++;
        }

        return deleted;
    }

    private static string SafeKey(string sourceId) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(sourceId)).Replace('+', '-').Replace('/', '_');
}
