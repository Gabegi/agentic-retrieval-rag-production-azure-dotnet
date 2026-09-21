using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;
using AgenticRagApp.Infrastructure.Clients.Blob;
using AgenticRagApp.Infrastructure.Clients.Search;
using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

// Decides what this run has to extract, before a single paid analyze call is made: list the
// container cheaply, read what the index already holds, and compare the two.
//
// Moved out of ExtractionService unchanged - the decision logic is worth testing on its own,
// separately from the sequencing (extract -> run state -> report) that ExtractionService is
// now reduced to.
public class IndexDiffService : IIndexDiffService
{
    private readonly BlobContainerClient       _container;
    private readonly IBlobStore                _blobStore;
    private readonly IIndexDocumentService     _indexDocumentService;
    private readonly ILogger<IndexDiffService> _logger;

    public IndexDiffService(
        BlobContainerClient       container,
        IBlobStore                blobStore,
        IIndexDocumentService     indexDocumentService,
        ILogger<IndexDiffService> logger)
    {
        _container            = container;
        _blobStore            = blobStore;
        _indexDocumentService = indexDocumentService;
        _logger               = logger;
    }

    public async Task<IndexDiff> FindDocsNotInIndexAsync(
        bool forceReindex, CancellationToken ct = default)
    {
        // What documents exist in blob storage right now - id + LastModified + the blob's own
        // metadata, no download or content yet. This is the "source" side of the diff.
        var sourceListing = await ListDocumentsInBlobAsync(ct);

        // What documents are already in the Search index - id + last-indexed date. This is
        // the "target" side. Diffing it against sourceListing below is what lets us skip
        // paying for extraction on anything already indexed and unchanged.
        var indexedDates = await _indexDocumentService.GetCurrentlyIndexedDocsIdsNDatesAsync(ct);

        // An empty map means every source document compares as new below and the whole corpus is
        // re-extracted at full Content Understanding cost. That is CORRECT after a recreate and
        // after a first run in a fresh environment, and it is the D180 defect otherwise - but the
        // three are indistinguishable from here, so this stage does not try to tell them apart.
        //
        // EmptyIndexStateException used to throw on the difference, using GetStatisticsAsync as
        // the second opinion. Removed 2026-09-17: it only ran on no-flag runs (the daily timer is
        // force+recreate, which was carved out), so it never covered a run that actually happens,
        // and its evidence was the service's own document count - which Azure documents as
        // approximate and which was observed drifting 3,734 -> 3,739 across forty idle minutes.
        // A guard asserting a contradiction cannot rest on a number that is allowed to be wrong.
        // See docs/2609/260918/per-step-blob-storage-findings.md §6c/§6d.
        if (indexedDates.Count == 0 && sourceListing.Count > 0)
            _logger.LogWarning(
                "Index-state read returned no documents — all {Count} source document(s) will be treated as new and re-extracted at full cost. Expected after a recreate or on a first run; otherwise this is the D180 shape.",
                sourceListing.Count);

        // We extract a document if either:
            // 1. It's new — sourceId isn't in indexedDates at all, or
            // 2. It's updated — it is in indexedDates, but sourceListing's LastModified is newer than what's recorded there, or
            // 3. forceReindex is true — process everything regardless.
        var (sourceIdsToProcess, removedSourceIds, toDeleteChunks, newCount, updated, skipped) =
            CompareSourceListingToIndex(sourceListing, indexedDates, forceReindex);

        // Hands over the LastModified/ContentLength facts already gathered above, so
        // the orchestrator never has to list the container a second time.
        var entriesToProcess = sourceIdsToProcess.ToDictionary(
            id => id, id => sourceListing[id], StringComparer.OrdinalIgnoreCase);

        return new IndexDiff(
            entriesToProcess, removedSourceIds, toDeleteChunks,
            newCount, updated, skipped,
            SourceCount: sourceListing.Count, IndexedCount: indexedDates.Count);
    }

    // Cheap listing of every PDF blob's name + LastModified + ContentLength only — no download,
    // no analyze call. This is the "source" side of the diff; ExtractionService's extraction
    // loop does the expensive download + extraction, only for whatever
    // CompareSourceListingToIndex decides is actually needed, using this same data instead of
    // listing the container a second time.
    private async Task<Dictionary<string, PdfBlobInfo>> ListDocumentsInBlobAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, PdfBlobInfo>(StringComparer.OrdinalIgnoreCase);
        var blobs  = await _blobStore.ListBlobsAsync(_container, ct: ct);

        foreach (var (name, lastModified, contentLength, metadata) in blobs)
        {
            if (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) continue;

            // LastModified is a system property Azure Blob Storage sets automatically (updated whenever the blob's content changes)
            if (lastModified is null)
                _logger.LogWarning(
                    "'{Blob}' has no LastModified from blob storage — treating as never-modified so it isn't reprocessed every run.",
                    name);

            // The custom metadata was discarded here (`_`) until 2026-09-21. It is the sync's
            // zenya_* contract (ZenyaBlobLayout), decoded once per blob and carried on the entry so
            // extraction and chunking can stamp it without a second listing. Null when the blob
            // carries no zenya_document_id - i.e. every blob in the manual corpus - so the diff
            // and everything after it behave exactly as before on "protocols".
            var zenya = ZenyaMetadata.FromBlobMetadata(metadata);
            result[name] = new PdfBlobInfo(lastModified ?? DateTimeOffset.MinValue, contentLength, zenya.IsPresent ? zenya : null);
        }

        return result;
    }

    // Compares the cheap source listing (id + LastModified, no content) against what's already
    // indexed - BEFORE any extraction happens, so a doc that's unchanged never costs a paid
    // extraction call:
    // - not in the index yet                              -> new, process
    // - in the index, forceReindex or newer last_modified  -> updated, process, delete old chunks
    // - in the index, not newer and not forceReindex       -> skip
    // - in the index, but absent from this listing         -> removed, delete chunks
    // Because "removed" is judged against the full listing (every source id that
    // exists, regardless of whether it needed re-extraction) rather than against what
    // successfully extracted, a doc that merely fails extraction this run is never
    // mistaken for one withdrawn from the source.
    internal static (HashSet<string> SourceIdsToProcess, List<string> RemovedSourceIds, List<string> ToDeleteChunks,
        int NewCount, int Updated, int Skipped) CompareSourceListingToIndex(
            IReadOnlyDictionary<string, PdfBlobInfo> sourceListing,
            Dictionary<string, DateTimeOffset>       indexedDates,
            bool                                     forceReindex)
    {
        var sourceIdsToProcess = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var removedSourceIds   = new List<string>();
        var toDeleteChunks     = new List<string>();
        var newCount           = 0;
        var updated            = 0;
        var skipped            = 0;

        foreach (var (sourceId, entry) in sourceListing)
        {
            // checks if document ID is already indexed
            if (!indexedDates.TryGetValue(sourceId, out var indexedDate))
            {
                sourceIdsToProcess.Add(sourceId);
                newCount++;
                continue;
            }

            // if document is already indexed and not newer, skip adding it to the index
            if (!forceReindex && entry.LastModified <= indexedDate)
            {
                skipped++;
                continue;
            }

            toDeleteChunks.Add(sourceId);
            sourceIdsToProcess.Add(sourceId);
            updated++;
        }

        // Docs that were previously indexed but no longer appear in the source listing at all.
        var removedFromBlob = indexedDates.Keys
            .Where(id => !sourceListing.ContainsKey(id))
            .ToList();
        removedSourceIds.AddRange(removedFromBlob);
        toDeleteChunks.AddRange(removedFromBlob);

        return (sourceIdsToProcess, removedSourceIds, toDeleteChunks, newCount, updated, skipped);
    }
}
