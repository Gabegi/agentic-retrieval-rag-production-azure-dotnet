using Azure;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;
using AgenticRagApp.Infrastructure.Clients.Blob;
using AgenticRagApp.Indexing.Csv.Models;
using AgenticRagApp.Common.Models;
using AgenticRagApp.Observability;
using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Indexing.Csv.Services;

// CSV implementation of IExtractionOrchestrator.
// Downloads pages.csv + index.csv from blob storage, runs the full CSV pipeline,
// and returns source-agnostic ExtractionDocuments plus quality metadata from the validator.
public class CsvExtractionOrchestrator : IExtractionOrchestrator
{
    private readonly BlobContainerClient                _container;
    private readonly BlobContainerClient                _stateContainer;
    private readonly IBlobStore                         _blobStore;
    private readonly IRunReportWriter                   _reportWriter;
    private readonly ICsvExtractor                      _csvExtractor;
    private readonly ICsvJoiner                         _csvJoiner;
    private readonly IDataCleaner                       _dataCleaner;
    private readonly IPipelineValidator                 _pipelineValidator;
    private readonly ILogger<CsvExtractionOrchestrator> _logger;

    public string Source => "csv";

    private const string PagesBlobName = "zenya_pages.csv";
    private const string IndexBlobName = "zenya_index.csv";
    private const string StateBlobName = "csv-extraction-state.json";

    // Folder segment namespacing every report blob this orchestrator writes, so a
    // future second IExtractionOrchestrator (e.g. PDF) writing to the same
    // "pipeline-reports" container doesn't mix its blobs in with these.
    private const string ReportFolder = "indexing/csv-extraction";

    // Caps how many individual validation issues get their own log line. A badly
    // malformed file can produce thousands of near-identical issues - real log
    // volume/cost at that point, not useful signal. This is separate from the
    // Take(100) further down on the *returned* issues list, which exists for a
    // different reason (Durable Table Storage's 64KB row-size limit).
    private const int MaxLoggedIssues = 100;

    internal sealed record RunState(int CleanedRecords);

    public CsvExtractionOrchestrator(
        BlobContainerClient                container,
        BlobContainerClient                stateContainer,
        IBlobStore                         blobStore,
        IRunReportWriter                   reportWriter,
        ICsvExtractor                      csvExtractor,
        ICsvJoiner                         csvJoiner,
        IDataCleaner                       dataCleaner,
        IPipelineValidator                 pipelineValidator,
        ILogger<CsvExtractionOrchestrator> logger)
    {
        _container         = container;
        _stateContainer    = stateContainer;
        _blobStore         = blobStore;
        _reportWriter      = reportWriter;
        _csvExtractor      = csvExtractor;
        _csvJoiner         = csvJoiner;
        _dataCleaner       = dataCleaner;
        _pipelineValidator = pipelineValidator;
        _logger            = logger;
    }

    public async Task<ExtractionOutput> ExtractDocumentsAsync(bool overrideMagnitudeCheck = false, CancellationToken ct = default)
    {
        var runAt = DateTimeOffset.UtcNow;

        // Stream directly from blob storage instead of buffering the whole file into a
        // MemoryStream first - CsvExtractor only ever reads forward through the stream
        // once, so there's nothing gained by holding the entire file in memory before
        // parsing starts, and a large export would otherwise mean two full files
        // resident in memory at once for no reason.
        var pagesStreamTask = _blobStore.OpenReadAsync(_container, PagesBlobName, ct);
        var indexStreamTask = _blobStore.OpenReadAsync(_container, IndexBlobName, ct);
        await Task.WhenAll(pagesStreamTask, indexStreamTask);

        await using var pagesStream = await pagesStreamTask;
        await using var indexStream = await indexStreamTask;

        var pagesResult = _csvExtractor.ExtractPages(pagesStream);
        var indexResult = _csvExtractor.ExtractIndex(indexStream);
        var joinResult  = _csvJoiner.Join(pagesResult.Records, indexResult.Records);
        var cleanResult = _dataCleaner.Clean(joinResult.Joined);

        var (previousCount, previousETag) = await PreviousRunCount(ct);
        var report = _pipelineValidator.Validate(pagesResult, indexResult, joinResult, cleanResult, previousCount);

        // Write report in blob
        if (_reportWriter.IsEnabled)
        {
            await _reportWriter.WriteReportAsync(
                $"{ReportFolder}/{runAt:yyyy/MM/dd}/{runAt:HHmmssfff}-validation-report.json", report, ct);
        }

        var (effectivePassed, errors, warnings, missingTitle, missingVersion, missingDepartment) =
            LogAndEmitValidationTelemetry(report, cleanResult, overrideMagnitudeCheck);

        if (!effectivePassed)
            throw new InvalidOperationException(
                $"CSV validation failed ({report.ReconciliationProblems.Count} reconciliation problem(s), " +
                $"{report.MagnitudeWarnings.Count} magnitude warning(s)) — aborting extraction.");

        // Whether passed normally or via override, this becomes the new baseline - an
        // override run resets the magnitude check so the NEXT run is auto-gated again
        // instead of comparing against the same stale count.
        await SaveRunStateAsync(cleanResult.Records.Count, previousETag, ct);

        return BuildExtractionOutput(report, cleanResult, errors, warnings, missingTitle, missingVersion, missingDepartment);
    }

    // Maps the validated, cleaned records into the source-agnostic ExtractionOutput
    // returned to the caller - the actual "product" of this whole pipeline.
    private static ExtractionOutput BuildExtractionOutput(
        ValidationReport report,
        CleanResult      cleanResult,
        int              errors,
        int              warnings,
        int              missingTitle,
        int              missingVersion,
        int              missingDepartment)
    {
        var extractionDocs = cleanResult.Records
            .Select(r => new ExtractionDocument(
                SourceId: r.DocumentId,
                Ordinal:  r.PageIndex,
                Content:  r.PageContent,
                Metadata: new Dictionary<string, string>
                {
                    ["title"]              = r.Title,
                    ["version"]            = r.Version,
                    ["last_modified_date"] = r.LastModified.ToString("yyyy-MM-dd"),
                    ["check_date"]         = r.CheckDate?.ToString("o") ?? "",
                    ["quick_code"]         = r.QuickCode,
                    ["folder_path"]        = r.FolderPath,
                    ["document_type"]      = r.DocumentTypeName,
                    ["summary"]            = r.Summary,
                    ["relative_path"]      = r.RelativePath,
                }))
            .ToList();

        var issues = report.Issues
            .Take(100)  // cap to stay safely under Durable Table Storage 64KB limit
            .Select(i => new ValidationIssueEntry(i.Stage, i.Severity, i.DocumentId, i.Message))
            .ToList();

        var spotCheck = report.SpotCheckSample
            .Select(r => new SpotCheckEntry(
                r.DocumentId,
                r.Title,
                r.PageContent.Length > 300 ? r.PageContent[..300] + "…" : r.PageContent))
            .ToList();

        return new ExtractionOutput(extractionDocs)
        {
            ValidationErrors       = errors,
            ValidationWarnings     = warnings,
            ReconciliationProblems = report.ReconciliationProblems.Count,
            StaleDocCount          = report.StaleDocCount,
            MojibakeRepairedPages  = report.MojibakeRepairedPages,
            DetectedTableCount     = report.DetectedTableCount,
            DocsWithoutHeadings    = report.DocumentsNeedingFallbackChunking.Count,
            MissingTitleCount      = missingTitle,
            MissingVersionCount    = missingVersion,
            MissingDepartmentCount = missingDepartment,
            Issues                 = issues,
            RedFlags               = report.RedFlags.ToList(),
            SpotCheckSample        = spotCheck,
        };
    }

    // Everything this run logs and emits as metrics, in one place: whether the caller's
    // magnitude override actually applied, the magnitude-shift audit trail, a pass/fail
    // summary line, one log line per issue (capped), and the OTel counters
    // dashboards/alerts read. Runs regardless of pass/fail, before the caller's throw,
    // so a failed run still gets full telemetry. Returns effectivePassed plus the counts
    // ExtractionOutput needs further down, so callers don't have to recompute any of it.
    private (bool EffectivePassed, int Errors, int Warnings, int MissingTitle, int MissingVersion, int MissingDepartment)
        LogAndEmitValidationTelemetry(
            ValidationReport report,
            CleanResult      cleanResult,
            bool             overrideMagnitudeCheck)
    {
        // A magnitude-only failure can be deliberately let through by the caller; error-rate
        // and reconciliation failures never are, regardless of overrideMagnitudeCheck.
        var magnitudeOverrideApplied = !report.Passed && overrideMagnitudeCheck && report.PassedExcludingMagnitude;
        var effectivePassed          = report.Passed || magnitudeOverrideApplied;

        foreach (var warning in report.MagnitudeWarnings)
            _logger.LogWarning("{Warning}", warning);

        if (magnitudeOverrideApplied)
            _logger.LogWarning(
                "VALIDATION OVERRIDE APPLIED — magnitude-shift gate bypassed by explicit operator request. " +
                "{Cleaned} records this run. Warnings: {Warnings}",
                cleanResult.Records.Count, string.Join(" | ", report.MagnitudeWarnings));

        _logger.LogInformation("CSV validation {Result} — {Cleaned} records, {Issues} issues",
            effectivePassed ? "passed" : "failed", report.CleanedRecords, report.Issues.Count);

        foreach (var issue in report.Issues.Take(MaxLoggedIssues))
            _logger.Log(
                issue.Severity == "Error" ? LogLevel.Error : LogLevel.Warning,
                "[{Stage}] {DocId}: {Message}", issue.Stage, issue.DocumentId, issue.Message);
        if (report.Issues.Count > MaxLoggedIssues)
            _logger.LogWarning("…{More} more issue(s) not logged (see the run report for the full list).",
                report.Issues.Count - MaxLoggedIssues);

        var errors   = report.Issues.Count(i => i.Severity == "Error");
        var warnings = report.Issues.Count(i => i.Severity != "Error");

        var sourceTag = new KeyValuePair<string, object?>("source", Source);

        // Unconditional, not guarded with "> 0": these are counters, and a healthy run
        // (zero errors/stale docs) should still emit a zero data point. Otherwise a
        // dashboard/alert can't tell "this metric is healthy at zero" apart from "this
        // metric stopped reporting entirely".
        Instrumentation.ValidationIssues.Add(errors,   sourceTag, new("severity", "error"));
        Instrumentation.ValidationIssues.Add(warnings, sourceTag, new("severity", "warning"));
        Instrumentation.StaleDocs.Add(report.StaleDocCount, sourceTag);
        Instrumentation.DocsWithoutHeadings.Add(report.DocumentsNeedingFallbackChunking.Count, sourceTag);
        Instrumentation.MojibakeRepairedPages.Add(report.MojibakeRepairedPages, sourceTag);
        Instrumentation.DetectedTableCount.Record(report.DetectedTableCount, sourceTag);

        // Metadata completeness — count docs missing title, version, department. Title
        // and FolderPath are page-CSV fields and can legitimately vary page-to-page for
        // the same document, so a document only counts as "missing" if EVERY one of its
        // pages lacks that field, not just whichever page happens to come first.
        var byDocument        = cleanResult.Records.GroupBy(r => r.DocumentId).ToList();
        var missingTitle      = byDocument.Count(g => g.All(r => string.IsNullOrWhiteSpace(r.Title)));
        var missingVersion    = byDocument.Count(g => g.All(r => string.IsNullOrWhiteSpace(r.Version)));
        var missingDepartment = byDocument.Count(g => g.All(r => string.IsNullOrWhiteSpace(r.FolderPath)));

        Instrumentation.MissingMetadata.Add(missingTitle,      sourceTag, new("field", "title"));
        Instrumentation.MissingMetadata.Add(missingVersion,    sourceTag, new("field", "version"));
        Instrumentation.MissingMetadata.Add(missingDepartment, sourceTag, new("field", "department"));

        return (effectivePassed, errors, warnings, missingTitle, missingVersion, missingDepartment);
    }

    // Holds the count of cleaned records from the last successful run
    // Returns that count
    // If difference is more than x%, that's flagged and hard failed
    private async Task<(int? Count, ETag? ETag)> PreviousRunCount(CancellationToken ct)
    {
        var (state, etag) = await _blobStore.TryReadJsonWithETagAsync<RunState>(_stateContainer, StateBlobName, ct);
        return (state?.CleanedRecords, etag);
    }

    private Task SaveRunStateAsync(int cleanedRecords, ETag? previousETag, CancellationToken ct) =>
        _blobStore.SaveJsonWithETagAsync(_stateContainer, StateBlobName, new RunState(cleanedRecords), previousETag, ct);
}
