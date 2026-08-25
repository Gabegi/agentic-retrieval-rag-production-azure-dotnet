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

    public async Task<IndexDiff> FindDocsNotInIndexAsync(bool forceReindex, CancellationToken ct = default)
    {
        // What documents exist in blob storage right now - id + LastModified only, no
        // download or content yet. This is the "source" side of the diff.
        var sourceListing = await ListDocumentsInBlobAsync(ct);

        // What documents are already in the Search index - id + last-indexed date. This is
        // the "target" side. Diffing it against sourceListing below is what lets us skip
        // paying for extraction on anything already indexed and unchanged.
        var indexedDates = await _indexDocumentService.GetCurrentlyIndexedDocsIdsNDatesAsync(ct);

        // We extract a document if either:
            // 1. It's new â€” sourceId isn't in indexedDates at all, or
            // 2. It's updated â€” it is in indexedDates, but sourceListing's LastModified is newer than what's recorded there, or
            // 3. forceReindex is true â€” process everything regardless.
        // A document Zenya marks inactive (ZenyaMetadata.IsActive false) is excluded from
        // processing even if new/updated, and torn down like a removed one if it's currently
        // indexed - see CompareSourceListingToIndex.
        var (sourceIdsToProcess, removedSourceIds, toDeleteChunks, newCount, updated, skipped, inactive) =
            CompareSourceListingToIndex(sourceListing, indexedDates, forceReindex);

        // Hands over the LastModified/ContentLength/Zenya facts already gathered above, so
        // the orchestrator never has to list the container a second time.
        var entriesToProcess = sourceIdsToProcess.ToDictionary(
            id => id, id => sourceListing[id], StringComparer.OrdinalIgnoreCase);

        return new IndexDiff(
            entriesToProcess, removedSourceIds, toDeleteChunks,
            newCount, updated, skipped, inactive,
            SourceCount: sourceListing.Count, IndexedCount: indexedDates.Count);
    }

    // Cheap listing of every PDF blob's name + LastModified + ContentLength + Zenya metadata
    // only â€” no download, no analyze call. This is the "source" side of the diff;
    // ExtractionService's extraction loop does the expensive download + extraction,
    // only for whatever CompareSourceListingToIndex decides is actually needed, using this
    // same data instead of listing the container a second time.
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
                    "'{Blob}' has no LastModified from blob storage â€” treating as never-modified so it isn't reprocessed every run.",
                    name);

            result[name] = new PdfBlobInfo(lastModified ?? DateTimeOffset.MinValue, contentLength, ZenyaMetadata.FromBlobMetadata(metadata));
        }

        return result;
    }

    // Compares the cheap source listing (id + LastModified + Zenya metadata, no content)
    // against what's already indexed - BEFORE any extraction happens, so a doc that's
    // unchanged never costs a paid extraction call:
    // - Zenya-inactive (ZenyaMetadata.IsActive false)      -> never processed; torn down like removed if currently indexed
    // - not in the index yet                              -> new, process
    // - in the index, forceReindex or newer last_modified  -> updated, process, delete old chunks
    // - in the index, not newer and not forceReindex       -> skip
    // - in the index, but absent from this listing         -> removed, delete chunks
    // Because "removed" is now judged against the full listing (every source id that
    // exists, regardless of whether it needed re-extraction) rather than against what
    // successfully extracted, a doc that merely fails extraction this run is never
    // mistaken for one withdrawn from the source.
    internal static (HashSet<string> SourceIdsToProcess, List<string> RemovedSourceIds, List<string> ToDeleteChunks,
        int NewCount, int Updated, int Skipped, int Inactive) CompareSourceListingToIndex(
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
        var inactive           = 0;

        foreach (var (sourceId, entry) in sourceListing)
        {
            // Zenya says this document is no longer valid - never process it, and if it's
            // currently indexed, tear it down the same way a removed-from-blob doc would be.
            if (!entry.Zenya.IsActive)
            {
                inactive++;
                if (indexedDates.ContainsKey(sourceId))
                {
                    removedSourceIds.Add(sourceId);
                    toDeleteChunks.Add(sourceId);
                }
                continue;
            }

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

        // Docs that were previously indexed but no longer appear in the source listing at all
        // (an inactive-but-still-present doc was already handled and added above, so exclude
        // it here to avoid double-counting it as both "inactive" and "removed").
        var removedFromBlob = indexedDates.Keys
            .Where(id => !sourceListing.ContainsKey(id))
            .ToList();
        removedSourceIds.AddRange(removedFromBlob);
        toDeleteChunks.AddRange(removedFromBlob);

        return (sourceIdsToProcess, removedSourceIds, toDeleteChunks, newCount, updated, skipped, inactive);
    }
}
