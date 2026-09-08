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
        // and one summary line per run; a warning instead when no document reported a token map.
        if (output.BilledTokensByModel.Count > 0)
        {
            foreach (var (key, count) in output.BilledTokensByModel)
                Instrumentation.CuModelTokens.Add(count, sourceTag, new("usage_key", key));

            _logger.LogInformation(
                "Content Understanding model tokens this run: {Tokens}.",
                string.Join(", ", output.BilledTokensByModel.Select(kv => $"{kv.Key}={kv.Value}")));
        }
        else if (output.Docs.Count > 0)
        {
            // Says so out loud rather than logging nothing (2026-08-27). The silent-empty path
            // is the same failure shape TryGetUsage's swallowed exception was - a blank number
            // that reads as "not interesting" for as long as nobody goes looking. This is the
            // only per-model cost signal the run emits, and it is now also the input to the
            // extraction-parallelism sizing (docs/2608/260827/extraction-coverage-chunking-review.md,
            // Q4 Step 0), so its absence is a finding, not a non-event.
            _logger.LogWarning(
                "No per-model Content Understanding token map was reported this run ({Docs} document(s) extracted) - " +
                "per-model cost telemetry is blank, not zero. The CU meters (pages/contextualization tokens) " +
                "cannot substitute: contextualization tokens bill a flat 1,000 per page.",
                output.Docs.Count);
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

        // And the quality equivalent - "how well was this file actually read". Absent when the
        // response reported no word confidences (see BuildWordConfidences).
        var confidenceByBlob = output.WordConfidences.ToDictionary(
            c => c.BlobName, c => c.Summary, StringComparer.Ordinal);

        // And what the service says the document IS - CU's own generated summary, which the
        // pipeline paid for on every run since the CU switch and read nowhere until 2026-09-08
        // (A2). Absent when the response carried no Summary field.
        var summaryByBlob = output.Summaries.ToDictionary(
            s => s.BlobName, s => s.Summary, StringComparer.Ordinal);

        var fileFacts = output.Docs.Select(d => new
        {
            BlobName  = d.SourceId,
            d.Title,
            // The document's first declared heading, next to the extracted Title on purpose
            // (2026-08-27). MissingTitleCount only counts EMPTY titles; the CAO failure was a
            // title that was present and wrong - a copyright line on one document, a cover
            // slogan on another (see the 260827 review, Gap 3). Title-not-equal-to-first-heading
            // is the cheap signal for that, and it needs both values side by side. No rule reads
            // this yet: a heading-mismatch threshold would be a guess until the corpus says
            // what normal looks like.
            FirstHeading = d.Headings.Count > 0 ? d.Headings[0].Content : null,
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

            // ── The rest of the mapped structure (2026-08-27) ────────────────────────────
            // All of this was mapped and counted NOWHERE - no report, no meter, no log line -
            // so every corpus figure quoted in the 260827 review ("483 hyperlinks", "57 rowspan
            // / 151 colspan", "130 of 152 PageHeaders", "0 chart blocks") had to be counted by
            // hand out of the one raw-response capture. These are .Count calls on data the
            // mapper already produced; the point is that the next such question is answered by
            // reading a report instead of re-deriving it.
            Sections    = d.Sections.Count,
            Boilerplate = d.Boilerplate.Count,
            Hyperlinks  = d.Hyperlinks.Count,
            Annotations = d.Annotations.Count,
            Lines       = d.Lines.Count,

            // The direct before/after measure for the HTML-table work (review Gap 1): merged
            // cells are why GFM conversion was rejected as lossy, and TableDetector currently
            // routes none of these tables. Counted off the mapped spans rather than the markdown.
            MergedTableCells = d.Tables.Sum(t =>
                t.Cells.Count(c => c.RowSpan is not null || c.ColumnSpan is not null)),

            // Geometry coverage (2026-09-08). Every table and figure carries a Source string,
            // so these should equal Tables and Figures above; a drop back towards zero means
            // the parse regressed or the service stopped sending it, and that has to be
            // visible in a report rather than discovered when a highlight feature is built -
            // the regions cannot be re-read after the run, only re-bought.
            TablesWithRegions  = d.Tables.Count(t => t.Regions.Count > 0),
            FiguresWithRegions = d.Figures.Count(f => f.Regions is { Count: > 0 }),

            // "chart" / "mermaid" / "unknown", the service's own DocumentFigureKind. Q1 checked
            // by hand that this corpus has zero chart-like figures and concluded "nothing to
            // build until one appears" - this is what makes that a monitored condition rather
            // than a one-off manual check.
            FiguresByKind = d.Figures
                .GroupBy(f => f.Kind ?? "unknown", StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),

            // End-to-end wall clock for this document (download + analyze + map). Null only
            // when the run loop never timed it, which real runs cannot produce.
            DurationMs = durationByBlob.TryGetValue(d.SourceId, out var ms) ? ms : (long?)null,
            // What this document billed, in the service's own units. Null means the analysis
            // reported no usage for it - blank, not zero.
            BilledPagesStandard     = usageByBlob.GetValueOrDefault(d.SourceId)?.BilledPagesStandard,
            ContextualizationTokens = usageByBlob.GetValueOrDefault(d.SourceId)?.ContextualizationTokens,
            // The real per-model token bill for this document, keys verbatim as billed (added
            // 2026-08-27). The two fields above are CU METERS - ContextualizationTokens reads a
            // flat 1,000 per page on every document, so dividing it by wall clock says nothing
            // about load. THIS is the number that maps to the deployment's TPM ceiling, and per
            // document it also yields real-tokens-per-page. Null when the analysis reported no
            // usage at all; empty when it reported usage carrying no token map.
            TokensByModel = usageByBlob.GetValueOrDefault(d.SourceId)?.TokensByModel,

            // How well the service says it READ this document - the quality axis the pipeline
            // had none of (2026-08-27). Distribution only, no threshold and no flag: see
            // WordConfidenceSummary for why an invented "low confidence" line is not shipped
            // here. Null means the response carried no word confidences.
            WordConfidence = confidenceByBlob.GetValueOrDefault(d.SourceId),

            // What the service says this document IS, in its own words (A2, 2026-09-08).
            // Metadata only - deliberately not an indexed chunk, since a whole-document
            // summary would compete with real chunks for recall (260819 decision).
            // Text is capped at DocumentSummary.ReportTextCap with the truncation reported
            // next to it; the confidence is the ONE field confidence this response carries,
            // and A11 is where a threshold would eventually be calibrated against it - no
            // rule reads it yet. Grounding is a span COUNT: it says whether the summary was
            // anchored in the document at all, without carrying the spans.
            Summary               = summaryByBlob.GetValueOrDefault(d.SourceId)?.TextForReport,
            SummaryTruncated      = summaryByBlob.GetValueOrDefault(d.SourceId)?.TruncatedInReport,
            SummaryConfidence     = summaryByBlob.GetValueOrDefault(d.SourceId)?.Confidence,
            SummaryGroundingSpans = summaryByBlob.GetValueOrDefault(d.SourceId)?.GroundingSpanCount,
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
