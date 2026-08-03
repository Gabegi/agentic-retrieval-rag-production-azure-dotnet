using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Storage.Blobs;
using AgenticRagApp.Infrastructure.Clients.Blob;

namespace AgenticRagApp.Observability.Reports;

public class RunReportWriter : IRunReportWriter
{
    // JsonStringEnumConverter so PipelineStage/IssueSeverity serialise as names rather
    // than integers. These reports are read by humans and (soon) by a model; "Parse:Pages"
    // and "Error" carry meaning, 0 and 1 do not. The names on the wire are pinned by
    // [JsonStringEnumMemberName] on each enum member, so reordering the enum can't
    // silently change the report format.
    private static readonly JsonSerializerOptions s_opts = new()
    {
        WriteIndented = true,
        Converters    = { new JsonStringEnumConverter() },
    };

    private readonly IBlobStore         _blobStore;
    private readonly BlobContainerClient _container;

    // Always true - these are the small (few-KB) diagnostic reports (validation-report.json,
    // file-facts.json, the extraction diff, PdfIndexRunReport) that operators need to read in
    // Azure, not just locally. Previously gated to env.IsDevelopment(), which meant the one
    // environment where you can't attach a debugger was also the one environment with none of
    // these reports - same principle GetLastIndexStatsAsync/SaveLastIndexStatsAsync below
    // already followed ("drift detection is pointless if it only runs in dev"), now applied
    // consistently. Kept as a property (rather than deleting every "if (!IsEnabled) return"
    // call site) so this is the one place that decides it.
    public bool IsEnabled => true;

    public RunReportWriter(IBlobStore blobStore, BlobContainerClient container)
    {
        _blobStore = blobStore;
        _container = container;
    }

    public Task WriteReportAsync<T>(string path, T report, CancellationToken ct = default) =>
        WriteAsync(path, report, ct);

    private static string LastIndexStatsPath(string source) => $"indexing/_last-stats-{source}.json";

    public async Task<(long DocumentCount, long StorageSizeBytes)?> GetLastIndexStatsAsync(string source, CancellationToken ct = default)
    {
        try
        {
            var (stats, _) = await _blobStore.TryReadJsonWithETagAsync<LastIndexStats>(_container, LastIndexStatsPath(source), ct);
            return stats is null ? null : (stats.DocumentCount, stats.StorageSizeBytes);
        }
        catch
        {
            // Missing/corrupt baseline should never block the pipeline — just means no drift check this run.
            return null;
        }
    }

    public Task SaveLastIndexStatsAsync(string source, long documentCount, long storageSizeBytes, CancellationToken ct = default) =>
        WriteAsync(LastIndexStatsPath(source), new LastIndexStats(documentCount, storageSizeBytes), ct);

    private record LastIndexStats(long DocumentCount, long StorageSizeBytes);

    private async Task WriteAsync<T>(string path, T data, CancellationToken ct)
    {
        await _blobStore.EnsureContainerExistsAsync(_container, ct);
        var json = JsonSerializer.Serialize(data, s_opts);
        await _blobStore.UploadAsync(_container, path, BinaryData.FromString(json), overwrite: true, ct);
    }
}
