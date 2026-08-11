using System.Text.Json;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;
using AgenticRagApp.Infrastructure.Clients.Blob;
using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Observability.Reports;

// Builds the RunEmailSummary for one run: the run report blob plus its sibling stage reports,
// the previous run's pointer, and the corpus size. Called from SendReportEmailActivity, right
// after SaveIndexReportActivity/SaveRestoreReportActivity write the report it reads.
//
// Every sibling read is best-effort. A missing stage report degrades that section of the email;
// it never fails the send. What is NOT best-effort is the run report blob itself - if that can't
// be read there is nothing to report and the caller aborts.
//
// Reads no artifacts. Chunk diagnostics (zero-chunk document IDs, samples, duplicates) are
// computed in ChunkActivity and arrive on ChunkingStageMetrics - see
// docs/2608/260807/pipeline-report-content-candidates.md §3.
public sealed class RunReportAssembler
{
    public const string LastRunPointerPath = "_last-run.json";
    private const string LatestEvalResultsPointerPath = "_latest-eval-results.json";
    private const int MaxNamesRendered = 20;

    private readonly IBlobStore          _blobStore;
    private readonly BlobContainerClient _reports;
    private readonly BlobContainerClient _documents;
    private readonly ReportEmailOptions  _options;
    private readonly ILogger<RunReportAssembler> _logger;

    public RunReportAssembler(
        IBlobStore blobStore,
        BlobContainerClient reports,
        BlobContainerClient documents,
        ReportEmailOptions options,
        ILogger<RunReportAssembler> logger)
    {
        _blobStore = blobStore;
        _reports   = reports;
        _documents = documents;
        _options   = options;
        _logger    = logger;
    }

    public async Task<RunEmailSummary?> AssembleAsync(RunReportRef path, string blobName, CancellationToken ct)
    {
        var found   = new List<string>();
        var missing = new List<string>();

        if (path.Kind == RunReportKind.Restore)
        {
            var restore = await TryReadAsync<PdfRestoreRunReport>(blobName, ct);
            if (restore is null) return null;
            found.Add(blobName);

            return new RunEmailSummary
            {
                Kind           = RunReportKind.Restore,
                InstanceId     = path.InstanceId,
                BlobPath       = blobName,
                RestoreReport  = restore,
                Flags          = EvaluateRestoreFlags(restore),
                SourcesFound   = found,
                SourcesMissing = missing,
                Previous       = await TryReadAsync<PreviousRunPointer>(LastRunPointerPath, ct),
            };
        }

        var report = await TryReadAsync<PdfIndexRunReport>(blobName, ct);
        if (report is null) return null;
        found.Add(blobName);

        // Sibling reports are named {ts}-{reportName}-{instanceId}.json under a date folder. The
        // timestamp prefix isn't knowable here, so they're found by listing the day's folder and
        // matching on the report name + instance ID - which is exactly what the instance-ID
        // plumbing was for. Both the run's own date folder and the following day are searched:
        // the run report is filed under StartedAt while stage reports are named at
        // activity-execution time, so a run starting at 23:58 writes them into the next day's
        // folder.
        var validation = await FindSiblingAsync<Dictionary<string, JsonElement>>(path, "pdf-validation", ct);
        var fileFacts  = await FindSiblingAsync<List<Dictionary<string, JsonElement>>>(path, "pdf-file-facts", ct);
        var diff       = await FindSiblingAsync<Dictionary<string, JsonElement>>(path, "pdf-extraction-diff", ct);
        var failure    = await FindSiblingAsync<Dictionary<string, JsonElement>>(path, "pdf-failure", ct);

        Track(found, missing, "validation-report",  validation.Blob);
        Track(found, missing, "file-facts",         fileFacts.Blob);
        Track(found, missing, "extraction-diff",    diff.Blob);
        // A failure report only exists when extraction crashed before validation - its absence
        // on a healthy run is normal, so it is never reported as missing.
        if (failure.Blob is not null) found.Add(failure.Blob);

        var validationFacts = ParseValidation(validation.Value);
        var fileFactsSummary = ParseFileFacts(fileFacts.Value);
        var previous = await TryReadAsync<PreviousRunPointer>(LastRunPointerPath, ct);

        return new RunEmailSummary
        {
            Kind        = RunReportKind.Index,
            InstanceId  = path.InstanceId,
            BlobPath    = blobName,
            IndexReport = report,
            Validation  = validationFacts,
            FileFacts   = fileFactsSummary,
            Diff        = ParseDiff(diff.Value),
            Failure     = ParseFailure(failure.Value),
            CorpusDocumentCount = await TryCountCorpusAsync(ct),
            Previous     = previous,
            EvalBaseline = await TryReadEvalBaselineAsync(ct),
            Flags = FlagEvaluator.Evaluate(
                report, validationFacts, fileFactsSummary, previous, _options.CalibrationMode),
            SourcesFound   = found,
            SourcesMissing = missing,
        };
    }

    private static void Track(List<string> found, List<string> missing, string label, string? blob)
    {
        if (blob is null) missing.Add(label); else found.Add(blob);
    }

    private static IReadOnlyList<ReportFlag> EvaluateRestoreFlags(PdfRestoreRunReport r)
    {
        var flags = new List<ReportFlag>();

        if (!r.Success)
            flags.Add(new ReportFlag(FlagSeverity.Critical, "Restore.Success", "failed", "true",
                "The index was wiped and the rebuild did not complete — the index may be empty or partial.",
                "Check the error, then re-run StartRestore before anyone queries the system."));

        if (r.SnapshotInstanceId is null)
            flags.Add(new ReportFlag(FlagSeverity.Critical, "Restore.SnapshotInstanceId", "none", "a snapshot",
                "No snapshot existed for this source — there was nothing to restore from.",
                "Run a full indexing pass to rebuild the corpus and produce a snapshot."));

        if (r.ChunksFailed > 0)
            flags.Add(new ReportFlag(FlagSeverity.Critical, "Restore.ChunksFailed",
                r.ChunksFailed.ToString(), "0",
                "One or more chunks failed to upload during restore — the index is incomplete.",
                "Check the Search service logs for the upsert error, then re-run StartRestore."));

        if (r.ChunksMissingVector > 0)
            flags.Add(new ReportFlag(FlagSeverity.Warning, "Restore.ChunksMissingVector",
                r.ChunksMissingVector.ToString(), "0",
                "Restored without a cached vector — present as documents, but invisible to vector/hybrid search.",
                "Run an incremental indexing pass to re-embed them."));

        return flags;
    }

    // ── Sibling lookup ───────────────────────────────────────────────────────

    private async Task<(T? Value, string? Blob)> FindSiblingAsync<T>(
        RunReportRef path, string reportName, CancellationToken ct) where T : class
    {
        foreach (var date in new[] { path.Date, path.Date.AddDays(1) })
        {
            var prefix = $"{date:yyyy/MM/dd}/";
            try
            {
                var blobs = await _blobStore.ListBlobsAsync(_reports, prefix, ct);
                var match = blobs
                    .Select(b => b.Name)
                    .Where(n => n.Contains($"-{reportName}-{path.InstanceId}.json", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .LastOrDefault();

                if (match is null) continue;

                var value = await TryReadAsync<T>(match, ct);
                if (value is not null) return (value, match);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Listing '{Prefix}' failed while assembling the run email", prefix);
            }
        }

        return (null, null);
    }

    private async Task<T?> TryReadAsync<T>(string blobName, CancellationToken ct) where T : class
    {
        try
        {
            if (!await _blobStore.ExistsAsync(_reports, blobName, ct)) return null;
            return await _blobStore.DownloadJsonAsync<T>(_reports, blobName, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read '{Blob}' for the run email — that section will be omitted", blobName);
            return null;
        }
    }

    private async Task<long?> TryCountCorpusAsync(CancellationToken ct)
    {
        try
        {
            var blobs = await _blobStore.ListBlobsAsync(_documents, null, ct);
            return blobs.Count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not count the documents container — corpus size omitted");
            return null;
        }
    }

    // ── Parsers ──────────────────────────────────────────────────────────────
    // These read the reports as loose JSON rather than binding to their concrete types.
    // PdfQualityGateResult and the two anonymous-typed reports live in Indexing.Pdf, and the
    // diff/file-facts blobs have no declared type at all - a shape change there should degrade
    // one section of an email, not break the build or throw at send time.

    private static ValidationReportFacts? ParseValidation(Dictionary<string, JsonElement>? json)
    {
        if (json is null) return null;

        return new ValidationReportFacts(
            Passed:                   GetBool(json, "Passed"),
            ControlCharsStripped:     GetInt(json, "ControlCharsStripped"),
            InvisibleCharsStripped:   GetInt(json, "InvisibleCharsStripped"),
            LigaturesExpanded:        GetInt(json, "LigaturesExpanded"),
            HyphenationJoinsRepaired: GetInt(json, "HyphenationJoinsRepaired"),
            TableConversionFallbacks: GetInt(json, "TableConversionFallbacks"),
            MojibakeRepairedPages:    GetInt(json, "MojibakeRepairedPages"),
            DetectedTableCount:       GetInt(json, "DetectedTableCount"),
            MagnitudeWarnings:        GetStrings(json, "MagnitudeWarnings"),
            RedFlags:                 GetStrings(json, "RedFlags"),
            DocumentsNeedingFallbackChunking: GetStrings(json, "DocumentsNeedingFallbackChunking"));
    }

    private static FileFactsSummary? ParseFileFacts(List<Dictionary<string, JsonElement>>? rows)
    {
        if (rows is null || rows.Count == 0) return null;

        var histogram = new Dictionary<string, int>(StringComparer.Ordinal);
        double cost = 0;
        long   bytes = 0;
        int    withoutProducer = 0;

        foreach (var row in rows)
        {
            cost  += GetDouble(row, "EstimatedCostUsd");
            bytes += GetLong(row, "FileSizeBytes");

            var spec = GetString(row, "PdfSpecVersion") ?? "unknown";
            histogram[spec] = histogram.GetValueOrDefault(spec) + 1;

            // NativeMetadata is a nested object; a missing Producer marks a non-standard export
            // path, which is the only reason these per-file facts are aggregated at all.
            if (!row.TryGetValue("NativeMetadata", out var meta)
                || meta.ValueKind != JsonValueKind.Object
                || !meta.TryGetProperty("Producer", out var producer)
                || producer.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                || string.IsNullOrWhiteSpace(producer.GetString()))
                withoutProducer++;
        }

        return new FileFactsSummary(rows.Count, cost, bytes, withoutProducer, histogram);
    }

    private static ExtractionDiffFacts? ParseDiff(Dictionary<string, JsonElement>? json)
    {
        if (json is null) return null;

        var removed   = GetStrings(json, "RemovedSourceIds");
        var processed = GetStrings(json, "ProcessedSourceIds");

        return new ExtractionDiffFacts(
            NewCount:     GetInt(json, "NewCount"),
            Updated:      GetInt(json, "Updated"),
            Skipped:      GetInt(json, "Skipped"),
            RemovedCount: GetInt(json, "RemovedCount"),
            RemovedSourceIds:   removed.Take(MaxNamesRendered).ToList(),
            ProcessedSourceIds: processed.Take(MaxNamesRendered).ToList(),
            NamesTruncated:     removed.Count > MaxNamesRendered || processed.Count > MaxNamesRendered);
    }

    private static FailureReportFacts? ParseFailure(Dictionary<string, JsonElement>? json)
    {
        if (json is null) return null;

        var stack = GetString(json, "StackTrace");

        return new FailureReportFacts(
            RunAt:         json.TryGetValue("RunAt", out var r) && r.TryGetDateTimeOffset(out var dt) ? dt : default,
            ExceptionType: GetString(json, "ExceptionType") ?? "(unknown)",
            Message:       GetString(json, "Message") ?? "",
            // Truncated: a full stack trace crowds out everything else in an email body.
            StackTraceExcerpt: stack is null ? null : string.Join('\n', stack.Split('\n').Take(15)));
    }

    // ── Eval baseline ────────────────────────────────────────────────────────

    // Written by .pipelines/templates/eval-publish-results.yml right after it uploads the run's
    // results.jsonl - lets this read the latest eval baseline in one call instead of listing,
    // now that eval results share this container with every other report and no longer sit
    // under their own "eval-results/" prefix.
    private sealed record EvalResultsPointer(string Path, DateTimeOffset RanAt);

    // Deliberately tolerant of absence: if no eval has ever run, or the container is not
    // reachable, the section is simply omitted. A run email must never depend on whether
    // someone happened to run the eval tests.
    private async Task<EvalBaseline?> TryReadEvalBaselineAsync(CancellationToken ct)
    {
        try
        {
            var pointer = await TryReadAsync<EvalResultsPointer>(LatestEvalResultsPointerPath, ct);
            if (pointer is null) return null;

            var bytes = await _blobStore.DownloadBytesAsync(_reports, pointer.Path, ct);
            var rows  = System.Text.Encoding.UTF8.GetString(bytes)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => { try { return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line); } catch { return null; } })
                .Where(r => r is not null)
                .Select(r => r!)
                .ToList();

            if (rows.Count == 0) return null;

            // -1 is the "not scored for this scenario type" sentinel (Answer vs Refusal) - it
            // must never be averaged in, or every mean is dragged below its true value.
            static double? Mean(List<Dictionary<string, JsonElement>> rows, string field)
            {
                var scored = rows.Select(r => GetDouble(r, field)).Where(v => v >= 0).ToList();
                return scored.Count == 0 ? null : scored.Average();
            }

            return new EvalBaseline(
                ExecutionId:   System.IO.Path.GetFileNameWithoutExtension(pointer.Path),
                RanAt:         pointer.RanAt,
                ScenarioCount: rows.Count,
                FailedCount:   rows.Count(r => !GetBool(r, "Succeeded")),
                MeanGroundedness:  Mean(rows, "Groundedness"),
                MeanRelevance:     Mean(rows, "Relevance"),
                MeanCoherence:     Mean(rows, "Coherence"),
                MeanEquivalence:   Mean(rows, "Equivalence"),
                MeanCitationMatch: Mean(rows, "CitationMatch"),
                MeanRefusalScore:  Mean(rows, "RefusalScore"),
                MeanContextTokens: Mean(rows, "ContextTokens"),
                TotalCostUsd:      rows.Sum(r => GetDouble(r, "CostUsd")));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogInformation(ex, "No answer-quality eval baseline available — section omitted");
            return null;
        }
    }

    // ── JSON helpers ─────────────────────────────────────────────────────────

    private static bool GetBool(Dictionary<string, JsonElement> j, string key) =>
        j.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.True;

    private static int GetInt(Dictionary<string, JsonElement> j, string key) =>
        j.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static long GetLong(Dictionary<string, JsonElement> j, string key) =>
        j.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i) ? i : 0;

    private static double GetDouble(Dictionary<string, JsonElement> j, string key) =>
        j.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : 0;

    private static string? GetString(Dictionary<string, JsonElement> j, string key) =>
        j.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static IReadOnlyList<string> GetStrings(Dictionary<string, JsonElement> j, string key) =>
        j.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : [];
}
