using Microsoft.Extensions.Logging;
using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Observability;
using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Indexing.CU.Services;

// Everything the extraction stage says about itself: the OpenTelemetry counters, the log lines
// and the report blobs. Split out of ExtractionService for the same reason ChunkingReporter was
// split out of ChunkingService - the stage sequences the work, it does not also format and write
// the account of it.
//
// It holds no run state of its own: ExtractionService calls ReportAsync exactly once, from its
// finally block, with everything the run produced (or the exception it died on).
public sealed class ExtractionReporter
{
    private const string DiffReportName      = "pdf-extraction-diff";
    private const string FileFactsReportName = "pdf-file-facts";
    private const string FailureReportName   = "pdf-failure";
    private const string RawCaptureName      = "cu-raw-response";

    // See CsvExtractionOrchestrator.MaxLoggedIssues - same rationale (log volume/cost cap,
    // separate from the returned-issues cap, which exists for Durable's row-size limit).
    private const int MaxLoggedIssues = 100;

    private readonly IRunReportWriter            _reportWriter;
    private readonly ILogger<ExtractionReporter> _logger;

    public ExtractionReporter(IRunReportWriter reportWriter, ILogger<ExtractionReporter> logger)
    {
        _reportWriter = reportWriter;
        _logger       = logger;
    }

    // Everything this run emits and writes, in one place, called from ExtractAsync's finally
    // block so it runs whether the run succeeded or threw.
    //
    // Each piece is caught independently: a transient blob error writing a report must not mask
    // the real exception the try block is already propagating, and must not stop the other
    // pieces from running.
    //
    // CancellationToken.None throughout, deliberately: a run cancelled mid-flight is exactly the
    // run whose diagnostics are worth having, and passing the (already-cancelled) token would
    // guarantee they are never written.
    public async Task ReportAsync(
        string source, DateTimeOffset runAt, string? instanceId, IndexDiff diff,
        PdfExtractionOutput? output, Exception? failure)
    {
        try
        {
            EmitMetrics(source, diff, output);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to emit extraction telemetry for run at {RunAt}.", runAt);
        }

        if (!_reportWriter.IsEnabled) return;

        if (output is not null)
        {
            try
            {
                await WriteReportsAsync(source, runAt, instanceId, diff, output, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write extraction reports for run at {RunAt}.", runAt);
            }
        }
        else if (failure is not null)
        {
            // Nothing was produced at all - write a minimal failure report so the run still
            // leaves something behind instead of silence.
            try
            {
                await WriteFailureReportAsync(runAt, instanceId, failure, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write extraction failure report for run at {RunAt}.", runAt);
            }
        }
    }

    // The run's one raw CU response (see ExtractionService._rawCapture): the service's own JSON
    // for one document, verbatim - what CUHelper's typed mapping is designed and re-verified
    // against. Parsed and re-embedded as a JsonElement so WriteReportAsync's serializer emits
    // it as JSON rather than one escaped string. Best-effort like every other report piece:
    // caught, logged, never fails the run.
    public async Task WriteRawCaptureAsync(
        string blobName, string rawJson, DateTimeOffset runAt, string? instanceId)
    {
        if (!_reportWriter.IsEnabled) return;

        try
        {
            using var parsed = System.Text.Json.JsonDocument.Parse(rawJson);
            await _reportWriter.WriteReportAsync(
                StageReportPath.Build(RawCaptureName, runAt, instanceId),
                new { BlobName = blobName, Response = parsed.RootElement },
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write the cu-raw-response capture for '{Blob}'.", blobName);
        }
    }

    private void EmitMetrics(string source, IndexDiff diff, PdfExtractionOutput? output)
    {
        Instrumentation.DocsSkipped.Add(diff.Skipped);
        Instrumentation.DocsNew.Add(diff.NewCount);
        Instrumentation.DocsUpdated.Add(diff.Updated);
        Instrumentation.DocsDeleted.Add(diff.RemovedSourceIds.Count);

        if (output is null) return;

        Instrumentation.DocsExtracted.Add(
            output.Docs.Select(d => d.SourceId).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var sourceTag = new KeyValuePair<string, object?>("source", source);

        Instrumentation.ValidationIssues.Add(output.ValidationErrors,   sourceTag, new("severity", "error"));
        Instrumentation.ValidationIssues.Add(output.ValidationWarnings, sourceTag, new("severity", "warning"));
        Instrumentation.DocsWithoutHeadings.Add(output.DocsWithoutHeadings, sourceTag);
        Instrumentation.DetectedTableCount.Record(output.DetectedTableCount, sourceTag);
        Instrumentation.MissingMetadata.Add(output.MissingTitleCount, sourceTag, new("field", "title"));

        // The cleaning counters that used to be emitted here (mojibake pages, control chars,
        // ligatures, hyphenation joins, line wraps) are gone with PdfCleaner. Its per-run
        // counted passes do not exist any more - the equivalent repairs happen inside the
        // extraction mapper and are not counted. Nothing is emitted rather than emitting
        // zeros, so a dashboard reading these does not show a sudden clean-corpus success.

        foreach (var issue in output.Issues.Take(MaxLoggedIssues))
            _logger.Log(
                issue.IsError ? LogLevel.Error : LogLevel.Warning,
                "[{Stage}] {DocId}: {Message}", issue.Stage, issue.DocumentId, issue.Message);
        if (output.Issues.Count > MaxLoggedIssues)
            _logger.LogWarning("…{More} more issue(s) not logged (see the run report for the full list).",
                output.Issues.Count - MaxLoggedIssues);

        foreach (var flag in output.RedFlags)
            _logger.LogWarning("{RedFlag}", flag);

        // What the service says this run actually cost, in the units it bills in. Null means no
        // usage was readable at all, which is worth distinguishing from a run that billed
        // nothing.
        if (output.BilledPagesStandard is { } pages)
        {
            _logger.LogInformation(
                "Content Understanding usage this run: {Pages} standard page(s), {Tokens} contextualization token(s).",
                pages, output.BilledContextualizationTokens ?? 0);

            Instrumentation.CuAnalyzePages.Add(pages, sourceTag);
            Instrumentation.CuContextualizationTokens.Add(output.BilledContextualizationTokens ?? 0, sourceTag);
        }
        else
        {
            _logger.LogWarning(
                "No Content Understanding usage was reported this run - cost telemetry is blank, not zero.");
        }

        // The per-model half of the bill (plan 1.3) - the number that maps to the
        // AI-deployment spend, keys verbatim as the service bills them. One meter add per key
        // and one summary line per run; empty when no document reported a token map.
        if (output.BilledTokensByModel.Count > 0)
        {
            foreach (var (key, count) in output.BilledTokensByModel)
                Instrumentation.CuModelTokens.Add(count, sourceTag, new("usage_key", key));

            _logger.LogInformation(
                "Content Understanding model tokens this run: {Tokens}.",
                string.Join(", ", output.BilledTokensByModel.Select(kv => $"{kv.Key}={kv.Value}")));
        }

        // Per-document analyze duration into the histogram (plan 2.2) - recorded here off the
        // already-lifted Durations rather than in the run loop, so the loop stays untouched.
        // Azure Monitor derives p50/p95/max; the per-document numbers themselves are in the
        // file-facts report.
        foreach (var duration in output.Durations)
            Instrumentation.CuAnalyzeDuration.Record(duration.DurationMs / 1000.0, sourceTag);

        LogContentHashes(output);
    }

    // The distinct-vs-total evidence the content hash exists to produce - see
    // ExtractionOutputBuilder.BuildContentHashes for why a cache keyed on it was removed, and what
    // these numbers would have to show before one comes back. Logged here rather than computed
    // here: the hashes arrive already built, this only says what they add up to.
    //
    // The build id is stamped alongside, because "distinct by bytes" is only comparable across
    // runs of the same extraction code - a mapper change makes the same bytes produce different
    // output, which is half of what killed the cache.
    //
    // Duplicates are logged AND raised as a red flag upstream. Deliberate rather than redundant:
    // the flag gets attention in the run report and the run analysis, this line names every group
    // uncapped for whoever then goes looking.
    private void LogContentHashes(PdfExtractionOutput output)
    {
        var hashes = output.ContentHashes;
        if (hashes.Count == 0) return;

        var distinct = hashes.Select(h => h.Hash).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        _logger.LogInformation(
            "Content hashes (build {Version}): {Total} document(s) hashed, {Distinct} distinct by bytes. Per-document hashes are in the {Report} report.",
            ExtractionVersion.AssemblyVersion, hashes.Count, distinct, FileFactsReportName);

        if (distinct == hashes.Count) return;

        var groups = hashes
            .GroupBy(h => h.Hash, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => string.Join(" == ", g.Select(h => h.BlobName)));

        _logger.LogWarning(
            "Byte-identical duplicates in the corpus ({Total} document(s), {Distinct} distinct): {Groups}",
            hashes.Count, distinct, string.Join(" | ", groups));
    }

    // The diff report (source IDs only, never document content, so this stays small regardless
    // of corpus size) plus a per-document facts report.
    private async Task WriteReportsAsync(
        string source, DateTimeOffset runAt, string? instanceId, IndexDiff diff,
        PdfExtractionOutput output, CancellationToken ct)
    {
        var diffReport = new
        {
            Source             = source,
            diff.NewCount,
            diff.Updated,
            diff.Skipped,
            RemovedCount       = diff.RemovedSourceIds.Count,
            RemovedSourceIds   = diff.RemovedSourceIds,
            ProcessedSourceIds = output.Docs.Select(d => d.SourceId).Distinct().ToList(),
        };

        await _reportWriter.WriteReportAsync(
            StageReportPath.Build(DiffReportName, runAt, instanceId), diffReport, ct);

        // Per-document facts, for corpus-level QA across a run. The PdfPig-derived columns this
        // report used to carry (file size, PDF spec version, native Producer/Creator/Subject/
        // Keywords) are gone with the preflight that read them; what replaces them is the
        // service's own usage, which is the number that actually matters per document now.

        // Every hashed document, keyed for the row build below. Includes files that hashed and
        // then failed to analyze; those have no row here, but the lookup costs nothing and the
        // duplicate red flag can still name them.
        var hashByBlob = output.ContentHashes.ToDictionary(
            h => h.BlobName, h => h.Hash, StringComparer.Ordinal);

        // Same keying as the hashes. This is where the run's wall clock finally becomes
        // attributable per document - the answer to "which file made extraction slow".
        var durationByBlob = output.Durations.ToDictionary(
            d => d.BlobName, d => d.DurationMs, StringComparer.Ordinal);

        // And the money's equivalent - "which file costs the bill". Absent when the service
        // reported no usage for that document (see BuildUsages).
        var usageByBlob = output.Usages.ToDictionary(
            u => u.BlobName, u => u, StringComparer.Ordinal);

        var fileFacts = output.Docs.Select(d => new
        {
            BlobName  = d.SourceId,
            d.Title,
            // SHA-256 over the raw bytes. This report is where the content-hash evidence actually
            // lands - the run log says how many were distinct, this says which document was which,
            // and it survives log retention. Null would mean the document extracted without ever
            // being hashed, which cannot happen today.
            ContentHash = hashByBlob.GetValueOrDefault(d.SourceId),
            // Distinct pages, not span entries - a page can carry several verbatim CU ranges.
            PageCount = d.PageSpans.Select(s => s.PageNumber).Distinct().Count(),
            Headings  = d.Headings.Count,
            Tables    = d.Tables.Count,
            Figures   = d.Figures.Count,
            FiguresWithDescription = d.Figures.Count(f => !string.IsNullOrWhiteSpace(f.Description)),
            ContentChars = d.Content.Length,
            // End-to-end wall clock for this document (download + analyze + map). Null only
            // when the run loop never timed it, which real runs cannot produce.
            DurationMs = durationByBlob.TryGetValue(d.SourceId, out var ms) ? ms : (long?)null,
            // What this document billed, in the service's own units. Null means the analysis
            // reported no usage for it - blank, not zero.
            BilledPagesStandard     = usageByBlob.GetValueOrDefault(d.SourceId)?.BilledPagesStandard,
            ContextualizationTokens = usageByBlob.GetValueOrDefault(d.SourceId)?.ContextualizationTokens,
        }).ToList();

        await _reportWriter.WriteReportAsync(
            StageReportPath.Build(FileFactsReportName, runAt, instanceId), fileFacts, ct);
    }

    private sealed record ExtractionFailureReport(
        DateTimeOffset RunAt, string ExceptionType, string Message, string? StackTrace);

    private Task WriteFailureReportAsync(
        DateTimeOffset runAt, string? instanceId, Exception failure, CancellationToken ct) =>
        _reportWriter.WriteReportAsync(
            StageReportPath.Build(FailureReportName, runAt, instanceId),
            new ExtractionFailureReport(
                runAt, failure.GetType().FullName ?? failure.GetType().Name, failure.Message, failure.StackTrace),
            ct);
}
