using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AgenticRagApp.Infrastructure.Clients.Zenya.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    private readonly ZenyaHarvester _harvester;

    public ZenyaSyncService(
        IZenyaClient zenya,
        IZenyaDocumentStore store,
        ZenyaSyncOptions options,
        ILogger<ZenyaSyncService> logger,
        TimeProvider? time = null,
        ZenyaHarvester? harvester = null)
    {
        _zenya = zenya;
        _store = store;
        _options = options;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        // Optional only so the existing construction sites (tests) keep compiling; the harvest
        // itself is not optional - a null here still harvests, through the same client.
        _harvester = harvester ?? new ZenyaHarvester(zenya, NullLogger<ZenyaHarvester>.Instance, _time);
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
        // With every include_* block on: the typed item drives the flow below exactly as before,
        // the raw row rides into the sidecar (D243 Part 1).
        await foreach (var (item, listingRow) in _zenya.ListDocumentsWithBlocksAsync(states: null, ct))
        {
            c.Listed++;
            if (!seen.Add(item.DocumentId))
            {
                _logger.LogWarning("Zenya listed document {DocumentId} twice; second occurrence ignored.", item.DocumentId);
                continue;
            }

            var blobs = stored.GetValueOrDefault(item.DocumentId) ?? [];
            // "Unchanged" is decided on the BINARY's version. A sidecar carries the same version
            // metadata, but a document whose sidecar exists and whose binary does not is a
            // half-written document, not an unchanged one.
            if (blobs.Any(b => !ZenyaBlobLayout.IsSidecar(b.Name) && b.Version == item.Version))
            {
                c.Unchanged++;
                if (_options.Reharvest && !dryRun)
                    await ReharvestAsync(item, listingRow, c, ct);
                continue;
            }

            await SyncDocumentAsync(item, blobs, listingRow, dryRun, c, ct);
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

        // Tenant-level facts, once per run, after the walk so the folder ids are the ones seen on
        // the documents harvested this run (D243 Part 2, _tenant/). Only when something was
        // harvested: a run where nothing changed must stay at one listing call and write nothing,
        // and the previous run's tenant file still stands. Never on a dry run.
        if (!dryRun && c.Harvested > 0)
        {
            try
            {
                var tenant = await _harvester.HarvestTenantAsync(c.FolderIds, ct);
                await UploadJsonAsync(ZenyaBlobLayout.TenantBlobNameFor(_time.GetUtcNow()), tenant, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                c.HarvestFailed++;
                _logger.LogError(ex, "Tenant harvest failed; per-document sidecars are unaffected.");
            }
        }

        var result = c.ToResult(dryRun, stopwatch.Elapsed);
        _logger.LogInformation(
            "Sync {Mode} done in {Elapsed}: listed {Listed}, new {New}, changed {Changed}, unchanged {Unchanged}, removed {Removed}, authored-skipped {Authored}, not-downloadable {NotDownloadable}, failed {Failed}, metadata-dropped {MetadataDropped}, harvested {Harvested}, harvest-failed {HarvestFailed}, bytes {Bytes}.",
            dryRun ? "dry run" : "run", result.Elapsed, result.Listed, result.New, result.Changed, result.Unchanged,
            result.Removed, result.AuthoredSkipped, result.NotDownloadable, result.Failed, result.MetadataDropped,
            result.Harvested, result.HarvestFailed, result.BytesDownloaded);
        return result;
    }

    // Everything Zenya says about the document, into meta/{id}.json (D243 Part 2). A failure
    // here is counted and logged but never fails the document: the binary still syncs, and the
    // sidecar is retried on the next run that touches the document (or on --reharvest).
    private async Task HarvestAsync(ZenyaDocumentMetadata document, JsonElement? listingRow, Counters c, CancellationToken ct)
    {
        try
        {
            var harvest = await _harvester.HarvestDocumentAsync(
                document.DocumentId, document.Version, hasBinary: document.CanDownloadBinary == true, listingRow, ct);
            await UploadJsonAsync(ZenyaBlobLayout.MetaBlobNameFor(document.DocumentId), harvest, ct);
            c.Harvested++;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            c.HarvestFailed++;
            _logger.LogWarning(ex, "Harvest of {DocumentId} '{Title}' failed; the document itself continues.", document.DocumentId, document.Title);
        }
    }

    // An unchanged document under --reharvest: one metadata call for the routing flag, then the
    // sidecar. No download - the binary is already current.
    private async Task ReharvestAsync(ZenyaDocumentListItem item, JsonElement? listingRow, Counters c, CancellationToken ct)
    {
        try
        {
            var document = await _zenya.GetDocumentAsync(item.DocumentId, ct);
            if (document.Folder?.FolderId is { } folderId) c.FolderIds.Add(folderId);
            await HarvestAsync(document, listingRow, c, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            c.HarvestFailed++;
            _logger.LogWarning(ex, "Reharvest of {DocumentId} '{Title}' failed at metadata.", item.DocumentId, item.Title);
        }
    }

    // Sidecars carry zenya_document_id + zenya_version so the container listing recognises them
    // as managed (not foreign) and the removal pass deletes them with their document. They are
    // never counted as the binary: see IsSidecar at every place the sync reasons about blobs.
    private Task UploadJsonAsync(string blobName, ZenyaHarvest harvest, CancellationToken ct)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ZenyaBlobLayout.DocumentIdKey] = harvest.DocumentId,
        };
        if (harvest.Version is { } v) metadata[ZenyaBlobLayout.VersionKey] = v.ToString();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(harvest.ToJson()));
        return _store.UploadAsync(blobName, stream, "application/json", metadata, ct);
    }

    private async Task SyncDocumentAsync(ZenyaDocumentListItem item, List<StoredZenyaBlob> blobs, JsonElement? listingRow, bool dryRun, Counters c, CancellationToken ct)
    {
        var isNew = !blobs.Any(b => !ZenyaBlobLayout.IsSidecar(b.Name));
        var stage = "metadata";
        try
        {
            var document = await _zenya.GetDocumentAsync(item.DocumentId, ct);
            if (document.Folder?.FolderId is { } folderId) c.FolderIds.Add(folderId);

            // Before the routing decision, so the authored-only documents - the ones with no
            // binary and therefore nothing else in the container - get a sidecar too.
            if (!dryRun) await HarvestAsync(document, listingRow, c, ct);

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
            // A dropped key is data loss, so it is reported rather than left to be discovered as
            // an absent field months later (D204: absent must never be indistinguishable from
            // "never fetched").
            var metadata = ZenyaBlobLayout.BuildMetadata(document, contentType, _time.GetUtcNow(),
                key =>
                {
                    c.MetadataDropped++;
                    _logger.LogWarning("{DocumentId} '{Title}': dropped blob metadata {Key} to stay inside the {Budget}-byte limit.",
                        document.DocumentId, document.Title, key, ZenyaBlobLayout.MetadataByteBudget);
                });
            await _store.UploadAsync(blobName, buffer, contentType, metadata, ct);
            c.BytesDownloaded += buffer.Length;
            c.CountExtension(Path.GetExtension(blobName).TrimStart('.'));

            // A type change leaves the previous blob under another name; it must not survive as
            // a second SourceId for the same document.
            foreach (var stale in blobs.Where(b => !ZenyaBlobLayout.IsSidecar(b.Name) && !string.Equals(b.Name, blobName, StringComparison.Ordinal)))
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
        public int Listed, New, Changed, Unchanged, Removed, AuthoredSkipped, NotDownloadable, Failed, ForeignBlobs, PdfWithoutMagic, MetadataDropped, Harvested, HarvestFailed;
        public long BytesDownloaded;
        public readonly Dictionary<string, int> ByExtension = new(StringComparer.Ordinal);
        public readonly List<ZenyaSyncFailure> Failures = [];
        public readonly HashSet<int> FolderIds = [];

        public void Count(bool isNew) { if (isNew) New++; else Changed++; }
        public void CountExtension(string ext) => ByExtension[ext] = ByExtension.GetValueOrDefault(ext) + 1;

        public ZenyaSyncResult ToResult(bool dryRun, TimeSpan elapsed) => new(
            dryRun, Listed, New, Changed, Unchanged, Removed, AuthoredSkipped, NotDownloadable, Failed,
            ForeignBlobs, PdfWithoutMagic, MetadataDropped, Harvested, HarvestFailed, BytesDownloaded, ByExtension, Failures, elapsed);
    }
}
