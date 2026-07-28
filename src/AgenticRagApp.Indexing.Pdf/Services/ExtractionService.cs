using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;
using AgenticRagApp.Infrastructure.Clients.Blob;
using AgenticRagApp.Infrastructure.Clients.Search;
using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Observability;
using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Indexing.Pdf.Services;

public class ExtractionService : IExtractionService
{
    private readonly BlobContainerClient        _container;
    private readonly IBlobStore                 _blobStore;
    private readonly IExtractionOrchestrator    _extractor;
    private readonly IIndexDocumentService      _indexDocumentService;
    private readonly IRunReportWriter           _reportWriter;
    private readonly ILogger<ExtractionService> _logger;

    private const string ReportFolder = "indexing/extraction-diff";

    public ExtractionService(
        BlobContainerClient        container,
        IBlobStore                 blobStore,
        IExtractionOrchestrator    extractor,
        IIndexDocumentService      indexDocumentService,
        IRunReportWriter           reportWriter,
        ILogger<ExtractionService> logger)
    {
        _container             = container;
        _blobStore              = blobStore;
        _extractor              = extractor;
        _indexDocumentService  = indexDocumentService;
        _reportWriter          = reportWriter;
        _logger                = logger;
    }

    // Orchestrates the whole step: cheaply list what's available, diff against the current
    // index state BEFORE paying for extraction, extract only what's new/changed, emit
    // telemetry, and assemble the stats returned to the caller.
    public async Task<(IReadOnlyList<PdfExtractionDocument> Docs, ExtractionResults Stats)> ExtractAsync(
        bool forceReindex, CancellationToken ct = default)
    {
        // What documents exist in blob storage right now - id + LastModified only, no
        // download or content yet. This is the "source" side of the diff.
        var sourceListing = await ListDocumentsInBlobAsync(ct);

        // What documents are already in the Search index - id + last-indexed date. This is
        // the "target" side. Diffing it against sourceListing below is what lets us skip
        // paying for extraction on anything already indexed and unchanged.
        var indexedDates = await _indexDocumentService.GetCurrentIndexedDocumentDatesAsync(ct);

        // We extract a document if either:
            // 1. It's new — sourceId isn't in indexedDates at all, or
            // 2. It's updated — it is in indexedDates, but sourceListing's LastModified is newer than what's recorded there, or
            // 3. forceReindex is true — process everything regardless.
        // A document Zenya marks inactive (ZenyaMetadata.IsActive false) is excluded from
        // processing even if new/updated, and torn down like a removed one if it's currently
        // indexed - see CompareSourceListingToIndex.
        var (sourceIdsToProcess, removedSourceIds, toDeleteChunks, newCount, updated, skipped, inactive) =
            CompareSourceListingToIndex(sourceListing, indexedDates, forceReindex);

        _logger.LogInformation(
            "Extraction diff — source '{Source}': {New} new, {Updated} updated, {Removed} removed, {Skipped} skipped, {Inactive} inactive (of {Total} available)",
            _extractor.Source, newCount, updated, removedSourceIds.Count, skipped, inactive, sourceListing.Count);

        // Only pays for extraction (Document Intelligence, etc.) on what's actually new/updated -
        // and hands over the LastModified/ContentLength/Zenya facts already gathered above, so
        // the orchestrator never has to list the container a second time.
        var entriesToProcess = sourceIdsToProcess.ToDictionary(
            id => id, id => sourceListing[id], StringComparer.OrdinalIgnoreCase);
        var extractionOutput = await _extractor.ExtractDocumentsAsync(entriesToProcess, ct);

        var diff = new DiffResult(
            _extractor.Source, extractionOutput, extractionOutput.Docs.ToList(), removedSourceIds, toDeleteChunks, newCount, updated, skipped);

        await EmitMetricsAndBuildReport(diff, ct);

        return (diff.ToProcess, BuildStats(diff));
    }

    // Cheap listing of every PDF blob's name + LastModified + ContentLength + Zenya metadata
    // only — no download, no Document Intelligence call. This is the "source" side of the
    // diff in ExtractAsync; PdfExtractionOrchestrator's ExtractDocumentsAsync does the
    // expensive download + extraction, only for whatever CompareSourceListingToIndex decides
    // is actually needed, using this same data (see entriesToProcess above) instead of
    // listing the container a second time.
    private async Task<Dictionary<string, PdfBlobInfo>> ListDocumentsInBlobAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, PdfBlobInfo>(StringComparer.OrdinalIgnoreCase);
        var blobs  = await _blobStore.ListBlobsAsync(_container, ct: ct);

        foreach (var (name, lastModified, contentLength, metadata) in blobs)
        {
            if (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) continue;

            if (lastModified is null)
                _logger.LogWarning(
                    "'{Blob}' has no LastModified from blob storage — treating as never-modified so it isn't reprocessed every run.",
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
    private static (HashSet<string> SourceIdsToProcess, List<string> RemovedSourceIds, List<string> ToDeleteChunks,
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

            // Indexed last_modified_date is stored date-only (see
            // PdfExtractionOrchestrator.BuildExtractionOutput) - truncating lastModified the
            // same way here keeps this comparison consistent with what's actually indexed;
            // comparing full blob-timestamp precision against a date-truncated indexed value
            // would make every unchanged doc look "updated" on every run.
            var lastModifiedDate = DateTimeOffset.Parse(entry.LastModified.ToString("yyyy-MM-dd"));

            // if document is already indexed and not newer, skip adding it to the index
            if (!forceReindex && lastModifiedDate <= indexedDate)
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

    // Emit instrumentation metrics from the diff result, and (dev-only) write a
    // diagnostic report blob - source IDs only, never the full PdfExtractionDocument
    // content, so this stays small regardless of corpus size.
    private async Task EmitMetricsAndBuildReport(DiffResult diff, CancellationToken ct)
    {
        Instrumentation.DocsExtracted.Add(diff.Output.Docs.Count);
        Instrumentation.DocsSkipped.Add(diff.Skipped);
        Instrumentation.DocsNew.Add(diff.NewCount);
        Instrumentation.DocsUpdated.Add(diff.Updated);
        Instrumentation.DocsDeleted.Add(diff.RemovedSourceIds.Count);

        if (!_reportWriter.IsEnabled) return;

        var runAt = DateTimeOffset.UtcNow;
        var report = new
        {
            diff.Source,
            diff.NewCount,
            diff.Updated,
            diff.Skipped,
            RemovedCount       = diff.RemovedSourceIds.Count,
            RemovedSourceIds   = diff.RemovedSourceIds,
            ProcessedSourceIds = diff.ToProcess.Select(d => d.SourceId).Distinct().ToList(),
        };

        await _reportWriter.WriteReportAsync(
            $"{ReportFolder}/{runAt:yyyy/MM/dd}/{runAt:HHmmssfff}-diff.json", report, ct);
    }

    // Assemble ExtractionResults to return to the activity
    private static ExtractionResults BuildStats(DiffResult diff) => new(
        Source:                 diff.Source,
        DocsToProcess:          diff.ToProcess.Count,
        DocsSkipped:            diff.Skipped,
        DocsNew:                diff.NewCount,
        DocsUpdated:            diff.Updated,
        DocsDeleted:            diff.RemovedSourceIds.Count,
        StaleDocumentIds:       diff.StaleDocumentIds,
        ValidationErrors:       diff.Output.ValidationErrors,
        ValidationWarnings:     diff.Output.ValidationWarnings,
        ReconciliationProblems: diff.Output.ReconciliationProblems,
        StaleDocCount:          diff.Output.StaleDocCount,
        MojibakeRepairedPages:  diff.Output.MojibakeRepairedPages,
        DetectedTableCount:     diff.Output.DetectedTableCount,
        DocsWithoutHeadings:    diff.Output.DocsWithoutHeadings,
        MissingTitleCount:      diff.Output.MissingTitleCount,
        MissingVersionCount:    diff.Output.MissingVersionCount,
        MissingDepartmentCount: diff.Output.MissingDepartmentCount,
        TraceabilityGapCount:   diff.Output.TraceabilityGapCount,
        Issues:                 diff.Output.Issues,
        RedFlags:               diff.Output.RedFlags,
        SpotCheckSample:        diff.Output.SpotCheckSample);

    private record DiffResult(
        string                   Source,
        PdfExtractionOutput         Output,
        List<PdfExtractionDocument> ToProcess,
        List<string>             RemovedSourceIds,
        List<string>             StaleDocumentIds,
        int                      NewCount,
        int                      Updated,
        int                      Skipped);
}
