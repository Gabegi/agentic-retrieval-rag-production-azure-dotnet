using System.Collections.Concurrent;
using Azure;
using Azure.AI.ContentUnderstanding;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;
using AgenticRagApp.Infrastructure.Clients.Blob;
using AgenticRagApp.Infrastructure.Clients.ContentUnderstanding;
using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Common.Models;
using AgenticRagApp.Observability;
using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Indexing.CU.Services;

// The extraction stage, end to end: decide what needs doing, do it, record what happened.
//
// "Do it" is the run loop below - how many documents are in flight at once, how long the run
// keeps submitting new ones, and what happens when one of them fails - together with the
// per-file work that loop runs: download the bytes, hash them, pay for the analysis, map the
// response. Both used to sit one class further down (behind IExtractionOrchestrator, then behind
// DocumentFileExtractor); they are here now because there was never a second implementation for
// either of those to abstract over, and the per-file half has exactly one caller - the loop four
// lines above it.
//
// Every per-file outcome comes back as an ExtractedFile, including the failures: an analysis that
// failed, a page map that could not be built, a response shape the mapper did not anticipate. A
// failed DOWNLOAD is the exception - it throws, and the loop catches it per file, so one
// unreadable blob cannot abort a run.
//
// There is no preflight: no local PDF open, no size gate, no page-count check. Every blob goes
// straight to download and analyze, and anything wrong with it (corrupt, encrypted, oversized,
// not really a PDF) comes back as a service error. That is a deliberate trade - the preflight
// cost a PdfPig dependency and a second parser's opinion about the file, and Content
// Understanding has to form its own opinion anyway.
//
// What is not here is what keeps this from being the whole stage in one file - and each of these
// is reachable without going through this class:
// - IndexDiffService              decides what to extract (listing, index state, comparison)
// - ContentAnalysisClient         the paid analyze call itself
// - ExtractionOutputBuilder       turns a run's results into the stage's output
// - ExtractionReporter            counters, log lines, report blobs
// - ExtractionStatsBuilder        what the stage returns: the documents to process, which of
//                                 them may be torn down, and the metrics row
public class ExtractionService : IExtractionService
{
    private readonly IIndexDiffService            _diffService;
    private readonly BlobContainerClient          _documentsContainer;
    private readonly IContentAnalysisClient       _analysisClient;
    private readonly BlobContainerClient          _stateContainer;
    private readonly IBlobStore                   _blobStore;
    private readonly ExtractionReporter           _reporter;
    private readonly ILogger<ExtractionService>   _logger;
    private readonly TimeSpan                     _corpusWallClockLimit;
    // Nullable for tests only; the DI registration always passes it. See the red flag below.
    private readonly ContentUnderstandingDefaultsState? _cuDefaultsState;

    // Which extractor ran, as reported in the log line, the metric tags and the stats row. A
    // constant rather than a property read off an injected pipeline: there is one source, and the
    // interface that used to carry this said as much itself ("this pipeline is PDF-only").
    private const string Source = "pdf";

    private const string StateBlobName = "pdf-extraction-state.json";

    // Each blob triggers a paid, rate-limited Content Understanding call; tune this against the
    // service's actual throttling limits before raising it.
    private const int MaxExtractionParallelism = 8;

    // Below host.json's durableTask.activityFunctionTimeout (60 minutes) - a fixed margin under
    // it so a file that is already mid-download/mid-analyze when the corpus wall clock is
    // checked still has room to finish before Durable's own timeout would redeliver and re-bill
    // the whole activity.
    private static readonly TimeSpan CorpusWallClockLimit = TimeSpan.FromMinutes(50);

    // The first analysis's raw response body this run, for the cu-raw-response report - the
    // capture CUHelper's typed mapping is verified against. One per run (first writer wins via
    // Interlocked), so memory never scales with the corpus; written in ExtractAsync alongside
    // the other reports.
    private RawCapture? _rawCapture;

    internal sealed record RawCapture(string BlobName, string Json);

    // The previous run's magnitude baseline.
    //
    // Counts extracted PAGES now; it counted cleaned page records before, which was the same
    // grain by a different name - the cleaner produced one record per page. A state blob written
    // by an older build carries {"CleanedRecords":N} and deserializes to ExtractedPages = 0,
    // which is harmless: nothing reads this value until the validation seam below is filled in,
    // and the first run after that writes a real number.
    internal sealed record RunState(int ExtractedPages);

    public ExtractionService(
        IIndexDiffService            diffService,
        BlobContainerClient          documentsContainer,
        IContentAnalysisClient       analysisClient,
        BlobContainerClient          stateContainer,
        IBlobStore                   blobStore,
        ExtractionReporter           reporter,
        ILogger<ExtractionService>   logger,
        TimeSpan?                    corpusWallClockLimit = null,
        ContentUnderstandingDefaultsState? cuDefaultsState = null)
    {
        _diffService          = diffService;
        _documentsContainer   = documentsContainer;
        _analysisClient       = analysisClient;
        _stateContainer       = stateContainer;
        _blobStore            = blobStore;
        _reporter             = reporter;
        _logger               = logger;
        _corpusWallClockLimit = corpusWallClockLimit ?? CorpusWallClockLimit;
        _cuDefaultsState      = cuDefaultsState;
    }

    // Orchestrates the whole step: cheaply diff what's available against the current index
    // state BEFORE paying for extraction, extract only what's new/changed, emit telemetry, and
    // assemble the stats returned to the caller.
    public async Task<(IReadOnlyList<PdfExtractionDocument> Docs, ExtractionStageMetrics Stats)> ExtractAsync(
        bool forceReindex, string? instanceId = null, CancellationToken ct = default)
    {
        // Listing, index-state read and comparison all happen in IndexDiffService - see its
        // own comments for the new/updated/skipped/removed/inactive rules.
        var diff = await _diffService.FindDocsNotInIndexAsync(forceReindex, ct);

        _logger.LogInformation(
            "Extraction diff — source '{Source}': {New} new, {Updated} updated, {Removed} removed, {Skipped} skipped (of {Total} available)",
            Source, diff.NewCount, diff.Updated, diff.RemovedSourceIds.Count, diff.Skipped, diff.SourceCount);

        var runAt = DateTimeOffset.UtcNow;

        // Captured rather than swallowed (it is rethrown below) so the finally block can still
        // write something for a run that failed before it produced any output at all.
        PdfExtractionOutput? extractionOutput = null;
        Exception?           failure          = null;

        try
        {
            // Only pays for extraction on what's actually new/updated - and the entries carry
            // the LastModified/ContentLength facts the diff already gathered, so nothing
            // below has to list the container a second time.
            //
            // The bag's order is nondeterministic; ExtractionOutputBuilder.BuildDocuments sorts
            // by blob name before anything downstream sees it, which is what keeps chunk ids
            // stable from run to run.
            var results = new ConcurrentBag<ExtractedFile>();

            // Largest file first, not dictionary (i.e. blob-listing/alphabetical) order.
            // Analysis time scales with page count, and this corpus is skewed: in run 5f5fac04
            // the 132-page largest document sorted 43rd of 51 alphabetically, so ~670 pages of
            // work were dispatched before it was even submitted and it then ran ~7 minutes
            // largely alone while the small tail drained. Longest-processing-time-first is the
            // standard greedy fix for exactly that shape. ContentLength (bytes) stands in for
            // page count here because it is the only size signal available before paying for
            // the analysis - it is a scheduling hint only, and a mis-ranked file costs nothing
            // but position. Null (size unknown) sorts last. Output order is unaffected: see the
            // BuildDocuments sort note above.
            var entriesLargestFirst = diff.EntriesToProcess
                .OrderByDescending(pair => pair.Value.ContentLength ?? -1)
                .ToList();

            await Parallel.ForEachAsync(
                entriesLargestFirst,
                new ParallelOptions { MaxDegreeOfParallelism = MaxExtractionParallelism, CancellationToken = ct },
                async (pair, cancellationToken) =>
                {
                    var name = pair.Key;

                    // Wall-clock per file, measured here because only the loop sees a file end
                    // to end (download + analyze + map). Run 5f5fac04's 707-second extraction
                    // was reconstructed from Durable's activity duration and page counts alone -
                    // the run left no per-document timing anywhere. This is that instrumentation:
                    // it rides ExtractedFile the way ContentHash does (measurement only) and
                    // lands in the per-document facts report.
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                    // A failed extraction for one blob must not abort the run - and under
                    // Parallel.ForEachAsync an uncaught exception would also cancel the other
                    // in-flight tasks, discarding paid calls mid-flight.
                    try
                    {
                        // Corpus-level wall-clock guard: a partial run that stops submitting new
                        // files here completes cleanly well inside Durable's
                        // activityFunctionTimeout; a run that keeps submitting until that timeout
                        // fires gets the WHOLE activity redelivered, re-billing every
                        // already-completed analysis in this run, not just whichever file was
                        // still in flight. Checked per file (not just once) since this loop runs
                        // MaxExtractionParallelism-wide and stays open for the whole corpus.
                        //
                        // Deliberately not recorded as a failure: this is an intentional,
                        // graceful stopping point, not a defect. Simply not extracting this file
                        // this run is enough - the pre-extraction diff never advances its indexed
                        // date, so it is picked up as new/updated again on the very next run.
                        if (DateTimeOffset.UtcNow - runAt > _corpusWallClockLimit)
                        {
                            _logger.LogWarning(
                                "'{Blob}' not submitted - corpus wall-clock limit ({Limit}) reached; stopping new submissions this run so the activity completes cleanly. Will be picked up on the next run.",
                                name, _corpusWallClockLimit);
                            return;
                        }

                        // One span per document (observability plan 2.4) - the per-file node
                        // under the stage span ExtractActivity starts, so the App Insights
                        // transaction view shows which documents a slow run spent its time on.
                        // Started after the wall-clock guard on purpose: a file that was never
                        // submitted is not a unit of work to trace.
                        using var span = Instrumentation.ActivitySource.StartActivity("cu.extract_document");
                        span?.SetTag("cu.blob", name);

                        var extracted = (await ExtractFileAsync(name, cancellationToken))
                            with { DurationMs = stopwatch.ElapsedMilliseconds };
                        span?.SetTag("cu.ok", extracted.Ok);
                        results.Add(extracted);

                        _logger.LogInformation(
                            "Extracted '{Blob}' in {ElapsedMs} ms (ok: {Ok}).",
                            name, extracted.DurationMs, extracted.Ok);
                    }
                    // Filtered on the token, not the exception type: Azure.Core surfaces an
                    // exhausted network timeout as a TaskCanceledException too, and rethrowing
                    // that here would abort the whole run - discarding every other in-flight paid
                    // call - over one slow file. Only a real cancellation stops the run; anything
                    // else falls through and is recorded as that file's error.
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Download or extraction failed for '{Blob}'; recording as a file-level error.", name);
                        results.Add(ExtractedFile.Failed(name, PipelineIssue.Error(
                            PipelineStage.ParsePages, name, ex.Message, reason: PdfOpenFailureReason.Unknown))
                            with { DurationMs = stopwatch.ElapsedMilliseconds });
                    }
                });

            var files = results.ToList();

            // EntriesToProcess is passed straight through: it already carries every blob's
            // LastModified facts from the pre-extraction listing, keyed by the same
            // names the loop just iterated.
            extractionOutput = ExtractionOutputBuilder.BuildExtractionOutput(files, diff.EntriesToProcess);

            var (previousCount, previousETag) = await PreviousRunCountAsync(ct);

            // ── VALIDATION SEAM — deliberately absent ────────────────────────────────────
            //
            // This is where the pipeline validation step ran, and where it will run again. It
            // read the extraction results and produced a quality-gate result: page-count
            // reconciliation between what was extracted and what was cleaned, an aggregate
            // error-rate evaluation, a magnitude check against previousCount below, a
            // duplicate-(document, page) assertion, and a spot-check sample.
            //
            // It was removed with PdfCleaner and PdfPipelineValidator rather than ported,
            // because half of what it reconciled (raw pages vs cleaned records) no longer
            // exists as two separate things to compare - Content Understanding returns one
            // assembled document and the mapper produces the page map directly from it. What
            // it should check instead is an open question, and a validator ported to check
            // conditions that can no longer occur would read as coverage while providing none.
            //
            // Prior art, for whoever fills this in: docs/archive/AgenticRagApp.Indexing.DI,
            // Services/Extraction/PdfPipelineValidator.cs. Note that validation there was
            // reported, never enforced - it warned and the run continued regardless.
            //
            // previousCount is read above and passed to nothing today; it is the magnitude
            // baseline that check would use.
            _ = previousCount;

            // The baseline for the next run's magnitude check, saved whether or not anything
            // validated this one - the alternative (only saving on a clean run) leaves the
            // baseline stuck at whatever the last clean run saw, permanently mis-sizing every
            // subsequent comparison.
            await SaveRunStateAsync(
                extractionOutput.Docs.Sum(d => d.PageSpans.Select(s => s.PageNumber).Distinct().Count()),
                previousETag, ct);
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            // One call, on both paths - see ExtractionReporter for why each piece inside it is
            // caught independently and why it writes with CancellationToken.None.
            await _reporter.ReportAsync(
                Source, runAt, instanceId, diff, extractionOutput, failure);

            // The run's one raw-response capture (see _rawCapture) - its own call because it is
            // diagnostics with a lifecycle of its own, not part of the run report proper.
            if (_rawCapture is { } capture)
                await _reporter.WriteRawCaptureAsync(capture.BlobName, capture.Json, runAt, instanceId);
        }

        // What the stage returns, assembled in one place - including the rule about which
        // documents may have their existing chunks torn down, which is not a formality: get it
        // wrong and a document disappears from the index. See ExtractionStatsBuilder.
        //
        // The CU defaults state red-flags only when the startup check failed or never ran; a
        // healthy verified/updated outcome stays in the log. (During bring-up it rode along on
        // every run - that is how the DefaultsNotSet root cause was finally seen - and was
        // demoted to failures-only after the first healthy run, 2026-08-25.)
        return ExtractionStatsBuilder.BuildResult(
            Source, diff, extractionOutput, forceReindex,
            _cuDefaultsState is null or { Ok: true } ? null : $"cu_model_defaults: {_cuDefaultsState.Summary}");
    }

    // --- One document ---------------------------------------------------------

    // One document, end to end: download the bytes, hash them, pay for the analysis, map the
    // response. Knows nothing about the run it is part of - no parallelism, no wall clock, no
    // corpus-level counting - so the only reason to change it is a change in what extracting a
    // single file means.
    //
    // Virtual for one reason: ExtractionServiceTests substitutes it, which is what lets every
    // diff/stats test drive a whole run without paying for an analysis. That is the only seam
    // left in this stage - below it sits the paid call.
    internal virtual async Task<ExtractedFile> ExtractFileAsync(string blobName, CancellationToken ct)
    {
        var bytes = await _blobStore.DownloadBytesAsync(_documentsContainer, blobName, ct);

        // Hashed before any analysis, so the value applies regardless of what the service does
        // with the file. SHA-256 over bytes already in memory is negligible next to the paid call
        // that follows.
        var contentHash = ExtractedFile.ComputeContentHash(bytes);

        return (await AnalyzeDocumentWithCUAsync(blobName, bytes, ct)) with { ContentHash = contentHash };
    }

    // The paid call. Takes bytes, so it never touches blob storage and is testable without one.
    //
    // Markdown plus billed usage, with the structure (page spans / headings / boilerplate /
    // tables / figures / annotations / hyperlinks / title) mapped from the TYPED response by
    // CUHelper - CU classifies, the helpers map; see CUHelper and
    // docs/2608/260826/cuhelper-typed-structure-plan.md. The markdown is handed downstream
    // VERBATIM - no stripping, no rewriting - and every offset in the structure addresses
    // exactly that string. Profile and Language stay null: nothing measures them on this
    // backend yet.
    private async Task<ExtractedFile> AnalyzeDocumentWithCUAsync(string blobName, byte[] bytes, CancellationToken ct)
    {
        _logger.LogInformation("Submitting '{Blob}' to Content Understanding.", blobName);

        ContentAnalysis analysis;
        try
        {
            analysis = await _analysisClient.AnalyzeAsync(bytes, ct);
        }
        catch (RequestFailedException ex)
        {
            // The submit or the LRO itself failed. Nothing is retried here - the SDK's own
            // WaitUntil.Completed handles transient poll failures, and a resubmit would re-bill
            // the analysis.
            _logger.LogWarning(ex,
                "Content Understanding analysis of '{Blob}' failed ({Status}).", blobName, ex.Status);

            return ExtractedFile.Failed(blobName, PipelineIssue.Error(
                PipelineStage.ParsePages, blobName,
                $"Content Understanding analysis failed: {ex.Message}",
                reason: PdfOpenFailureReason.Unknown));
        }

        var result = analysis.Result;

        // One raw response per run, first analysis wins - the capture CUHelper's typed mapping
        // is designed and re-verified against. Stored here, written by ExtractAsync's reporter
        // call (which has the run identity this per-file method deliberately does not).
        if (analysis.RawJson is not null)
            Interlocked.CompareExchange(ref _rawCapture, new RawCapture(blobName, analysis.RawJson), null);

        var warnings = (result.Warnings ?? [])
            .Select(w => PipelineIssue.Warning(
                PipelineStage.ParsePages, blobName, $"{w.Code}: {w.Message}"))
            .ToList();

        var document = result.Contents.OfType<DocumentContent>().FirstOrDefault();

        if (document is null)
        {
            // A successful, fully billed response carrying no document content. Not a mapping
            // defect - the analyzer returned something this pipeline cannot use at all.
            _logger.LogError(
                "Content Understanding returned no DocumentContent for '{Blob}' (analyzer '{AnalyzerId}').",
                blobName, result.AnalyzerId);

            return ExtractedFile.Failed(blobName, PipelineIssue.Error(
                PipelineStage.ParsePages, blobName,
                "Content Understanding returned no document content.",
                reason: PdfOpenFailureReason.UnexpectedContentFormat), warnings);
        }

        // Typed mapping plus the furniture strip - CUHelper's Warnings are the map-what's-there
        // degradations (missing typed collections, span misalignment, non-utf16 encoding), and
        // they ride the run report like any service warning.
        var mapped = CUHelper.Map(document, result.StringEncoding);

        warnings.AddRange(mapped.Warnings.Select(w =>
            PipelineIssue.Warning(PipelineStage.ParsePages, blobName, w)));

        return new ExtractedFile(
            true, blobName, mapped.Markdown,
            PageSpans: mapped.PageSpans, Structure: mapped.Structure, Title: mapped.Title,
            Profile: null, Language: null,
            Usage: analysis.Usage, Error: null, Warnings: warnings)
        {
            // Rides here rather than in the run loop (unlike DurationMs, which only the loop can
            // see): the mapper is what computed it, and this is the first record that outlives
            // the mapper's return value.
            WordConfidence = mapped.WordConfidence,
        };
    }

    // --- Run state ------------------------------------------------------------

    private async Task<(int? Count, ETag? ETag)> PreviousRunCountAsync(CancellationToken ct)
    {
        var (state, etag) = await _blobStore.TryReadJsonWithETagAsync<RunState>(_stateContainer, StateBlobName, ct);
        return (state?.ExtractedPages, etag);
    }

    private Task SaveRunStateAsync(int extractedPages, ETag? previousETag, CancellationToken ct) =>
        _blobStore.SaveJsonWithETagAsync(_stateContainer, StateBlobName, new RunState(extractedPages), previousETag, ct);
}
