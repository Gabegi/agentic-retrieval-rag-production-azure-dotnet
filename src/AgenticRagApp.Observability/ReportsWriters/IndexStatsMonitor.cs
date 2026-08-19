using Microsoft.Extensions.Logging;

namespace AgenticRagApp.Observability.Reports;

public class IndexStatsMonitor : IIndexStatsMonitor
{
    // Run-over-run doc-count swing beyond this is flagged as drift. Tune based on observed
    // corpus volatility — the source data doesn't churn more than this between runs today.
    private const double DriftThresholdPct = 0.15;

    private readonly IRunReportWriter             _reportWriter;
    private readonly ILogger<IndexStatsMonitor>   _logger;

    public IndexStatsMonitor(IRunReportWriter reportWriter, ILogger<IndexStatsMonitor> logger)
    {
        _reportWriter = reportWriter;
        _logger       = logger;
    }

    public async Task<IndexDriftCheck> RecordAndCheckDriftAsync(
        string source, long documentCount, long storageSizeBytes, CancellationToken ct = default)
    {
        Instrumentation.IndexDocumentCount.Record(documentCount);
        Instrumentation.IndexStorageSizeBytes.Record(storageSizeBytes);

        var redFlags = new List<string>();
        var previous = await _reportWriter.GetLastIndexStatsAsync(source, ct);
        if (previous is { DocumentCount: > 0 } baseline && documentCount > 0)
        {
            var deltaPct = (documentCount - baseline.DocumentCount) / (double)baseline.DocumentCount;
            if (Math.Abs(deltaPct) > DriftThresholdPct)
            {
                redFlags.Add($"index_doc_count_drift:{deltaPct:+0.0%;-0.0%} ({baseline.DocumentCount} -> {documentCount})");
                _logger.LogWarning("Index doc count drift detected: {Previous} -> {Current} ({DeltaPct:P1})",
                    baseline.DocumentCount, documentCount, deltaPct);
            }
        }

        // A zero count is NOT a corpus that emptied - it is the statistics API lagging the
        // writes that just happened. UploadService takes this snapshot immediately after
        // upload and says so in its own comment ("Azure Search stats lag live writes by
        // minutes"); the 260819 run read 0 having just uploaded 2,932 chunks and raised
        // index_doc_count_drift:-100.0% (2997 -> 0) against a corpus that had not changed.
        //
        // So a zero is neither compared nor persisted, and the two halves matter for
        // different reasons. Not comparing kills the false red flag. Not persisting is the
        // one that was actually dangerous: SaveLastIndexStatsAsync overwrites the baseline,
        // and the guard above only runs when the baseline is > 0 - so writing a lagged zero
        // silently disabled the NEXT run's drift check entirely, with nothing to say it had.
        // A skipped save leaves the last real baseline in place, which is the correct
        // reading: this run learned nothing about the corpus size, so it should not teach
        // the next one anything either.
        if (documentCount > 0)
        {
            // Read before this line, returned after it: SaveLastIndexStatsAsync overwrites the
            // baseline blob with this run's numbers, so `previous` is the only surviving copy of
            // what the previous run left behind. See IndexDriftCheck.
            await _reportWriter.SaveLastIndexStatsAsync(source, documentCount, storageSizeBytes, ct);
        }
        else
        {
            _logger.LogWarning(
                "Index stats read {DocumentCount} document(s) - treating as a lagging stats read, " +
                "not corpus loss: drift not checked and the baseline ({Previous}) left in place.",
                documentCount, previous?.DocumentCount);
        }

        return new IndexDriftCheck(
            RedFlags:                 redFlags,
            PreviousDocumentCount:    previous?.DocumentCount,
            PreviousStorageSizeBytes: previous?.StorageSizeBytes);
    }
}
