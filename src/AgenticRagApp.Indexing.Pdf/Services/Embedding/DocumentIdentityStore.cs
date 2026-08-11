using System.Text;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// One blob per SourceId under pipeline-artifacts/document-identity/ - same "one blob per
// key, not one shared file" shape as VectorCache, for the same reason (a read never races a
// concurrent write for a different document). SourceId is a blob name (can contain '/' and
// spaces - see PdfExtractionDocument.SourceId), so it's base64-encoded into the blob key
// rather than used as a path segment directly.
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

    private static string SafeKey(string sourceId) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(sourceId)).Replace('+', '-').Replace('/', '_');
}
