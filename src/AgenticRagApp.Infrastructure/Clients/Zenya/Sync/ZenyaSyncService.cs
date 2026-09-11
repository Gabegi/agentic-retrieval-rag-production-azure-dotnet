using System.Diagnostics;
using AgenticRagApp.Infrastructure.Clients.Zenya.Models;
using Microsoft.Extensions.Logging;

namespace AgenticRagApp.Infrastructure.Clients.Zenya.Sync;

// Mirrors Zenya's published document listing into the zenya-documents container (D175 A5,
// D185). Per run: list the container once, list Zenya once, then per document
//   same version already stored          -> unchanged (no metadata call, no download)
//   otherwise GET /documents/{id}        -> route on can_download_binary / can_download_content
//     binary                             -> /download -> pdf/ or docs/ blob with zenya_* metadata
//     content only                       -> counted, not written (A9 is not designed yet)
//   finally every stored document Zenya no longer lists -> deleted, so the index's
//   RemovedSourceIds path can drop its chunks (D175: the worst failure is serving withdrawn policy)
// Sequential on purpose: the first full run measures (A8) before anything is parallelised.
// Dry run walks and counts exactly the same way but downloads nothing and touches no blob.
//
// Zenya is authoritative at this layer (its version decides new/changed/unchanged); the index
// diff stays authoritative at the index layer. Neither writes the other's objects.
public sealed class ZenyaSyncService
{
    private readonly IZenyaClient _zenya;
    private readonly IZenyaDocumentStore _store;
    private readonly ZenyaSyncOptions _options;
    private readonly ILogger<ZenyaSyncService> _logger;
    private readonly TimeProvider _time;

    public ZenyaSyncService(
        IZenyaClient zenya,
        IZenyaDocumentStore store,
        ZenyaSyncOptions options,
        ILogger<ZenyaSyncService> logger,
        TimeProvider? time = null)
    {
        _zenya = zenya;
        _store = store;
        _options = options;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public async Task<ZenyaSyncResult> RunAsync(CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var dryRun = _options.DryRun;
        var c = new Counters();

        await _store.EnsureReadyAsync(ct);

        // Container state, keyed on zenya_document_id. A document can have more than one blob
        // only transiently (type change pdf -> docx before the stale one is deleted), so a list.
        var stored = new Dictionary<string, List<StoredZenyaBlob>>(StringComparer.OrdinalIgnoreCase);
        foreach (var blob in await _store.ListAsync(ct))
        {
            if (blob.DocumentId is null)
            {
                c.ForeignBlobs++;
                _logger.LogWarning("Blob {Blob} carries no {Key} metadata - not managed by the sync, left alone.", blob.Name, ZenyaBlobLayout.DocumentIdKey);
                continue;
            }
            (stored.TryGetValue(blob.DocumentId, out var list) ? list : stored[blob.DocumentId] = []).Add(blob);
        }
        _logger.LogInformation("Container holds {Documents} synced documents in {Blobs} blobs ({Foreign} foreign). Dry run: {DryRun}.",
            stored.Count, stored.Values.Sum(l => l.Count), c.ForeignBlobs, dryRun);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var item in _zenya.ListDocumentsAsync(states: null, ct))
        {
            c.Listed++;
            if (!seen.Add(item.DocumentId))
            {
                _logger.LogWarning("Zenya listed document {DocumentId} twice; second occurrence ignored.", item.DocumentId);
                continue;
            }

            var blobs = stored.GetValueOrDefault(item.DocumentId) ?? [];
            if (blobs.Any(b => b.Version == item.Version))
            {
                c.Unchanged++;
                continue;
            }

            await SyncDocumentAsync(item, blobs, dryRun, c, ct);
        }

        // Removal pass. Counted per document; a document may own more than one blob.
        foreach (var (documentId, blobs) in stored)
        {
            if (seen.Contains(documentId)) continue;
            c.Removed++;
            foreach (var blob in blobs)
            {
                _logger.LogInformation("{Action} {Blob}: document {DocumentId} is no longer in Zenya's listing.",
                    dryRun ? "Would remove" : "Removing", blob.Name, documentId);
                if (!dryRun) await _store.DeleteAsync(blob.Name, ct);
            }
        }

        var result = c.ToResult(dryRun, stopwatch.Elapsed);
        _logger.LogInformation(
            "Sync {Mode} done in {Elapsed}: listed {Listed}, new {New}, changed {Changed}, unchanged {Unchanged}, removed {Removed}, authored-skipped {Authored}, not-downloadable {NotDownloadable}, failed {Failed}, bytes {Bytes}.",
            dryRun ? "dry run" : "run", result.Elapsed, result.Listed, result.New, result.Changed, result.Unchanged,
            result.Removed, result.AuthoredSkipped, result.NotDownloadable, result.Failed, result.BytesDownloaded);
        return result;
    }

    private async Task SyncDocumentAsync(ZenyaDocumentListItem item, List<StoredZenyaBlob> blobs, bool dryRun, Counters c, CancellationToken ct)
    {
        var isNew = blobs.Count == 0;
        var stage = "metadata";
        try
        {
            var document = await _zenya.GetDocumentAsync(item.DocumentId, ct);

            if (document.CanDownloadBinary != true)
            {
                if (document.CanDownloadContent == true)
                {
                    c.AuthoredSkipped++;
                    _logger.LogInformation("Skipping {DocumentId} '{Title}' (type {Type}): authored content only, no binary - A9 route.",
                        document.DocumentId, document.Title, document.Type);
                }
                else
                {
                    c.NotDownloadable++;
                    _logger.LogWarning("Document {DocumentId} '{Title}' (type {Type}) exposes neither a binary nor contents.",
                        document.DocumentId, document.Title, document.Type);
                }
                return;
            }

            if (dryRun)
            {
                // Route is known from the metadata alone; the download is what a real run adds.
                _logger.LogInformation("Would write {DocumentId} '{Title}' v{Version} ({State}).",
                    document.DocumentId, document.Title, document.Version, isNew ? "new" : "changed");
                c.Count(isNew);
                return;
            }

            stage = "download";
            await using var download = await _zenya.DownloadAsync(document.DocumentId, document.Version, ct);
            // Buffered so the blob name can follow the response's content type and the first
            // bytes can be inspected before anything is written. Documents are megabytes, one at
            // a time - memory is not the constraint on this host.
            using var buffer = new MemoryStream();
            await download.Content.CopyToAsync(buffer, ct);

            var contentType = download.ContentType ?? document.MimeType;
            var blobName = ZenyaBlobLayout.BlobNameFor(document, download.ContentType);
            if (blobName.StartsWith(ZenyaBlobLayout.PdfPrefix, StringComparison.Ordinal)
                && !ZenyaBlobLayout.HasPdfMagic(buffer.GetBuffer().AsSpan(0, (int)Math.Min(buffer.Length, 8))))
            {
                c.PdfWithoutMagic++;
                _logger.LogWarning("{DocumentId} '{Title}' is served as PDF but the bytes do not start with %PDF.", document.DocumentId, document.Title);
            }

            stage = "upload";
            buffer.Position = 0;
            var metadata = ZenyaBlobLayout.BuildMetadata(document, contentType, _time.GetUtcNow());
            await _store.UploadAsync(blobName, buffer, contentType, metadata, ct);
            c.BytesDownloaded += buffer.Length;
            c.CountExtension(Path.GetExtension(blobName).TrimStart('.'));

            // A type change leaves the previous blob under another name; it must not survive as
            // a second SourceId for the same document.
            foreach (var stale in blobs.Where(b => !string.Equals(b.Name, blobName, StringComparison.Ordinal)))
            {
                _logger.LogInformation("Removing {Blob}: superseded by {NewBlob}.", stale.Name, blobName);
                await _store.DeleteAsync(stale.Name, ct);
            }

            c.Count(isNew);
            _logger.LogInformation("Wrote {Blob} v{Version} ({State}, {Bytes} bytes).", blobName, document.Version, isNew ? "new" : "changed", buffer.Length);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One document must not end the run: the failures are listed in the result and the
            // host exits non-zero when there are any.
            c.Failed++;
            c.Failures.Add(new ZenyaSyncFailure(item.DocumentId, item.Title, stage, ex.Message));
            _logger.LogError(ex, "Document {DocumentId} '{Title}' failed at {Stage}.", item.DocumentId, item.Title, stage);
        }
    }

    private sealed class Counters
    {
        public int Listed, New, Changed, Unchanged, Removed, AuthoredSkipped, NotDownloadable, Failed, ForeignBlobs, PdfWithoutMagic;
        public long BytesDownloaded;
        public readonly Dictionary<string, int> ByExtension = new(StringComparer.Ordinal);
        public readonly List<ZenyaSyncFailure> Failures = [];

        public void Count(bool isNew) { if (isNew) New++; else Changed++; }
        public void CountExtension(string ext) => ByExtension[ext] = ByExtension.GetValueOrDefault(ext) + 1;

        public ZenyaSyncResult ToResult(bool dryRun, TimeSpan elapsed) => new(
            dryRun, Listed, New, Changed, Unchanged, Removed, AuthoredSkipped, NotDownloadable, Failed,
            ForeignBlobs, PdfWithoutMagic, BytesDownloaded, ByExtension, Failures, elapsed);
    }
}
