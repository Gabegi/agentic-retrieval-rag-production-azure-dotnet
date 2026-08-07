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
        if (previous is { DocumentCount: > 0 } baseline)
        {
            var deltaPct = (documentCount - baseline.DocumentCount) / (double)baseline.DocumentCount;
            if (Math.Abs(deltaPct) > DriftThresholdPct)
            {
                redFlags.Add($"index_doc_count_drift:{deltaPct:+0.0%;-0.0%} ({baseline.DocumentCount} -> {documentCount})");
                _logger.LogWarning("Index doc count drift detected: {Previous} -> {Current} ({DeltaPct:P1})",
                    baseline.DocumentCount, documentCount, deltaPct);
            }
        }

        // Read before this line, returned after it: SaveLastIndexStatsAsync overwrites the
        // baseline blob with this run's numbers, so `previous` is the only surviving copy of
        // what the previous run left behind. See IndexDriftCheck.
        await _reportWriter.SaveLastIndexStatsAsync(source, documentCount, storageSizeBytes, ct);

        return new IndexDriftCheck(
            RedFlags:                 redFlags,
            PreviousDocumentCount:    previous?.DocumentCount,
            PreviousStorageSizeBytes: previous?.StorageSizeBytes);
    }
}
