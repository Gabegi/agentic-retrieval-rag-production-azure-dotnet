using Microsoft.Extensions.Logging;
using AgenticRagApp.Common.Models;
using AgenticRagApp.Infrastructure.Clients.Search;
using AgenticRagApp.Observability;
using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Indexing.Csv.Services;

public class CsvExtractionService : ICsvExtractionService
{
    private readonly ICsvExtractionOrchestrator    _extractor;
    private readonly IIndexDocumentService      _indexDocumentService;
    private readonly IRunReportWriter           _reportWriter;
    private readonly ILogger<CsvExtractionService> _logger;

    private const string ReportFolder = "indexing/extraction-diff";

    public CsvExtractionService(
        ICsvExtractionOrchestrator    extractor,
        IIndexDocumentService      indexDocumentService,
        IRunReportWriter           reportWriter,
        ILogger<CsvExtractionService> logger)
    {
        _extractor             = extractor;
        _indexDocumentService  = indexDocumentService;
        _reportWriter          = reportWriter;
        _logger                = logger;
    }

    // Orchestrates the whole step: extract, diff against the current index state,
    // emit telemetry, and assemble the stats returned to the caller.
    public async Task<(IReadOnlyList<ExtractionDocument> Docs, ExtractionStageMetrics Stats)> ExtractAsync(
        bool forceReindex, bool overrideMagnitudeCheck = false, CancellationToken ct = default)
    {
        // fetch all documents to process
        var extractionOutput = await _extractor.ExtractDocumentsAsync(overrideMagnitudeCheck, ct);

        // check what documents we have in the index already (sourceId + last-indexed)
        var indexedDates = await _indexDocumentService.GetCurrentlyIndexedDocsIdsNDatesAsync(ct);

        var (toProcess, removedSourceIds, toDeleteChunks, newCount, updated, skipped) =
            CompareNewDocsNCurrentIndex(extractionOutput.Docs, indexedDates, forceReindex);

        _logger.LogInformation(
            "Extraction diff — source '{Source}': {New} new, {Updated} updated, {Removed} removed, {Skipped} skipped",
            _extractor.Source, newCount, updated, removedSourceIds.Count, skipped);

        var diff = new DiffResult(_extractor.Source, extractionOutput, toProcess, removedSourceIds, toDeleteChunks, newCount, updated, skipped);

        await EmitMetricsAndBuildReport(diff, ct);

        return (diff.ToProcess, BuildStats(diff));
    }

    // Compares freshly extracted documents against what's already indexed:
    // - not in the index yet                              -> new, process
    // - in the index, forceReindex or newer last_modified  -> updated, process, delete old chunks
    // - in the index, not newer and not forceReindex       -> skip
    // - in the index, but absent from this extraction      -> removed, delete chunks
    private static (List<ExtractionDocument> ToProcess, List<string> RemovedSourceIds, List<string> ToDeleteChunks,
        int NewCount, int Updated, int Skipped) CompareNewDocsNCurrentIndex(
            IReadOnlyList<ExtractionDocument>      docs,
            Dictionary<string, DateTimeOffset>     indexedDates,
            bool                                   forceReindex)
    {
        var toProcess      = new List<ExtractionDocument>();
        var toDeleteChunks = new List<string>();
        var seenSourceIds  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var newCount       = 0;
        var updated        = 0;
        var skipped        = 0;

        // Grouped by SourceId (not iterated per ExtractionDocument) because a single
        // document can span several records - one per PDF page or CSV row, all sharing
        // one SourceId. The new/updated/skip/delete decision is document-level (same
        // last_modified_date across every record of that SourceId), so it must be made
        // once per document; only the resulting records fed into toProcess stay page/row-level.
        foreach (var group in docs.GroupBy(d => d.SourceId, StringComparer.OrdinalIgnoreCase))
        {
            var sourceId = group.Key;
            seenSourceIds.Add(sourceId);

            if (!indexedDates.TryGetValue(sourceId, out var indexedDate))
            {
                toProcess.AddRange(group);
                newCount++;
                continue;
            }

            if (!forceReindex)
            {
                var modifiedStr = group.First().Metadata.GetValueOrDefault("last_modified_date");
                if (DateTimeOffset.TryParse(modifiedStr, out var modifiedDate) && modifiedDate <= indexedDate)
                {
                    skipped++;
                    continue;
                }
            }

            toDeleteChunks.Add(sourceId);
            toProcess.AddRange(group);
            updated++;
        }

        // Docs that were previously indexed but no longer appear in the source
        var removedSourceIds = indexedDates.Keys.Where(id => !seenSourceIds.Contains(id)).ToList();
        toDeleteChunks.AddRange(removedSourceIds);

        return (toProcess, removedSourceIds, toDeleteChunks, newCount, updated, skipped);
    }

    // Emit instrumentation metrics from the diff result, and (dev-only) write a
    // diagnostic report blob - source IDs only, never the full ExtractionDocument
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

    // Assemble ExtractionStageMetrics to return to the activity
    private static ExtractionStageMetrics BuildStats(DiffResult diff) => new(
        Source:                 diff.Source,
        DocsToProcess:          diff.ToProcess.Count,
        DocsSkipped:            diff.Skipped,
        DocsNew:                diff.NewCount,
        DocsUpdated:            diff.Updated,
        DocsDeleted:            diff.RemovedSourceIds.Count,
        StaleDocumentIds:       diff.StaleDocumentIds,
        TraceabilityGapCount:   null, // CSV traces back to Zenya via relative_path instead - see ExtractionOutput's comment
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
        Issues:                 diff.Output.Issues,
        RedFlags:               diff.Output.RedFlags,
        SpotCheckSample:        diff.Output.SpotCheckSample);

    private record DiffResult(
        string                   Source,
        ExtractionOutput         Output,
        List<ExtractionDocument> ToProcess,
        List<string>             RemovedSourceIds,
        List<string>             StaleDocumentIds,
        int                      NewCount,
        int                      Updated,
        int                      Skipped);
}
