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
        TimeSpan?                    corpusWallClockLimit = null)
    {
        _diffService          = diffService;
        _documentsContainer   = documentsContainer;
        _analysisClient       = analysisClient;
        _stateContainer       = stateContainer;
        _blobStore            = blobStore;
        _reporter             = reporter;
        _logger               = logger;
        _corpusWallClockLimit = corpusWallClockLimit ?? CorpusWallClockLimit;
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
            "Extraction diff — source '{Source}': {New} new, {Updated} updated, {Removed} removed, {Skipped} skipped, {Inactive} inactive (of {Total} available)",
            Source, diff.NewCount, diff.Updated, diff.RemovedSourceIds.Count, diff.Skipped, diff.Inactive, diff.SourceCount);

        var runAt = DateTimeOffset.UtcNow;

        // Captured rather than swallowed (it is rethrown below) so the finally block can still
        // write something for a run that failed before it produced any output at all.
        PdfExtractionOutput? extractionOutput = null;
        Exception?           failure          = null;

        try
        {
            // Only pays for extraction on what's actually new/updated - and the entries carry
            // the LastModified/ContentLength/Zenya facts the diff already gathered, so nothing
            // below has to list the container a second time.
            //
            // The bag's order is nondeterministic; ExtractionOutputBuilder.BuildDocuments sorts
            // by blob name before anything downstream sees it, which is what keeps chunk ids
            // stable from run to run.
            var results = new ConcurrentBag<ExtractedFile>();

            await Parallel.ForEachAsync(
                diff.EntriesToProcess,
                new ParallelOptions { MaxDegreeOfParallelism = MaxExtractionParallelism, CancellationToken = ct },
                async (pair, cancellationToken) =>
                {
                    var name = pair.Key;

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

                        results.Add(await ExtractFileAsync(name, cancellationToken));
                    }
                    catch (OperationCanceledException)
                    {
                        throw; // real cancellation should still stop the run, not log as a file error
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Download or extraction failed for '{Blob}'; recording as a file-level error.", name);
                        results.Add(ExtractedFile.Failed(name, PipelineIssue.Error(
                            PipelineStage.ParsePages, name, ex.Message, reason: PdfOpenFailureReason.Unknown)));
                    }
                });

            var files = results.ToList();

            // EntriesToProcess is passed straight through: it already carries every blob's
            // LastModified and Zenya metadata from the pre-extraction listing, keyed by the same
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
                extractionOutput.Docs.Sum(d => d.PageSpans.Count), previousETag, ct);
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
        }

        // What the stage returns, assembled in one place - including the rule about which
        // documents may have their existing chunks torn down, which is not a formality: get it
        // wrong and a document disappears from the index. See ExtractionStatsBuilder.
        return ExtractionStatsBuilder.BuildResult(Source, diff, extractionOutput, forceReindex);
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
    // Raw markdown and nothing else. The response-to-pipeline mapping (page spans, structure,
    // title, profile, language) is deliberately NOT wired: prebuilt-documentSearch returns
    // service-side chunks and a summary rather than prebuilt-document's layout detail, so the old
    // CU mappers did not apply to it and were deleted rather than left half-connected. Chunking
    // downstream receives no structure until that is designed against a real response.
    //
    // Usage is null for the same reason it used to be populated: AnalyzeUsageDetails comes off the
    // Operation via GetUsage(), and this path keeps only operation.Value. Cost reporting
    // under-counts every run until that is threaded back through.
    private async Task<ExtractedFile> AnalyzeDocumentWithCUAsync(string blobName, byte[] bytes, CancellationToken ct)
    {
        _logger.LogInformation("Submitting '{Blob}' to Content Understanding.", blobName);

        AnalysisResult result;
        try
        {
            result = await _analysisClient.AnalyzeAsync(bytes, ct);
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

        return new ExtractedFile(
            true, blobName, document.Markdown,
            PageSpans: null, Structure: null, Title: null, Profile: null, Language: null,
            Usage: null, Error: null, Warnings: warnings);
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
