using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AgenticRagApp.Infrastructure.Clients.Blob;
using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Common.Models;
using AgenticRagApp.Observability;
using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Indexing.CU.Services;

// PDF implementation of IExtractionOrchestrator — mirrors CsvExtractionOrchestrator's
// shape (download -> extract -> clean -> validate -> report -> diff-ready output),
// adapted to the PDF pipeline. Extractors stay I/O-free; orchestrators own reporting —
// same split ExtractionService/CsvExtractionOrchestrator already follow for IRunReportWriter.
public class PdfExtractionPipeline : IExtractionOrchestrator
{
    private readonly BlobContainerClient                _container;
    private readonly BlobContainerClient                _stateContainer;
    private readonly IBlobStore                          _blobStore;
    private readonly IRunReportWriter                   _reportWriter;
    private readonly IPdfExtractor                      _extractor;
    private readonly IPdfCleaner                        _pdfCleaner;
    private readonly IPdfPipelineValidator               _validator;
    private readonly IHostEnvironment                    _env;
    private readonly ILogger<PdfExtractionPipeline>  _logger;
    private readonly TimeSpan                            _corpusWallClockLimit;

    public string Source => "pdf";

    // Report-name prefixes namespacing every report blob this orchestrator writes, so they
    // don't collide with CsvExtractionOrchestrator's report names in the same shared container.
    private const string ValidationReportName = "pdf-validation";
    private const string FileFactsReportName  = "pdf-file-facts";
    private const string FailureReportName    = "pdf-failure";

    // See CsvExtractionOrchestrator.MaxLoggedIssues — same rationale (log volume/cost cap,
    // separate from MaxReturnedIssues below, which caps the *returned* issues list for
    // Durable's row-size limit).
    private const int MaxLoggedIssues = 100;

    // Cap on PdfExtractionOutput.Issues to stay safely under Durable Table Storage's 64KB
    // row-size limit — a different constraint from MaxLoggedIssues above, which just caps
    // log volume/cost. Coincidentally the same number today; not the same knob.
    private const int MaxReturnedIssues = 100;

    // Each blob triggers a paid, rate-limited Document Intelligence call; tune this
    // against DI's actual throttling limits before raising it.
    private const int MaxExtractionParallelism = 8;

    private const string StateBlobName = "pdf-extraction-state.json";

    internal sealed record RunState(int CleanedRecords);

    public PdfExtractionPipeline(
        BlobContainerClient                container,
        BlobContainerClient                stateContainer,
        IBlobStore                         blobStore,
        IRunReportWriter                   reportWriter,
        IPdfExtractor                      extractor,
        IPdfCleaner                        cleaner,
        IPdfPipelineValidator              validator,
        IHostEnvironment                   env,
        ILogger<PdfExtractionPipeline> logger,
        TimeSpan?                          corpusWallClockLimit = null)
    {
        _container      = container;
        _stateContainer = stateContainer;
        _blobStore      = blobStore;
        _reportWriter   = reportWriter;
        _extractor      = extractor;
        _pdfCleaner        = cleaner;
        _validator      = validator;
        _env            = env;
        _logger         = logger;
        _corpusWallClockLimit = corpusWallClockLimit ?? CorpusWallClockLimit;
    }

    public async Task<PdfExtractionOutput> ExtractDocumentsAsync(
        IReadOnlyDictionary<string, PdfBlobInfo> sourceIdsToProcess,
        string? instanceId = null,
        CancellationToken ct = default)
    {
        var runAt = DateTimeOffset.UtcNow;

        // Captured as the pipeline progresses so the finally block below can always emit
        // telemetry and write a report off whatever got built, regardless of where the try
        // block throws (the reconciliation-gate abort, BuildExtractionOutput). Nothing to
        // emit if Validate itself never ran (blob listing/cleaning failed) - there's no
        // report or cleanResult to build either from.
        PdfQualityGateResult?           validation  = null;
        PdfCleanResult?                cleanResult = null;
        List<PdfExtractionResult>?     fileResults = null;
        Exception?                     failure     = null;

        try
        {
            // 1/ Extract Data from PDFs, cleaning each file's pages as soon as its own
            // extraction finishes (finding #14) rather than waiting for the whole corpus -
            // see ExtractPdfsFromBlobAsync's per-file cleaning inside the parallel loop.
            Dictionary<string, DateTimeOffset> lastModifiedByBlob;
            Dictionary<string, ZenyaMetadata>  zenyaByBlob;
            (fileResults, cleanResult, lastModifiedByBlob, zenyaByBlob) = await ExtractPdfsFromBlobAsync(sourceIdsToProcess, runAt, ct);

            // 3/ Validate results
            var (previousCount, previousETag) = await PreviousRunCount(ct);
            validation = _validator.Validate(fileResults, cleanResult, previousRunCleanedCount: previousCount);

            // 4/ Validation is reported, not enforced: PdfPipelineValidator.Passed reflects
            // two independent evaluations (reconciliation problems, and the aggregate error
            // rate exceeding its threshold), but neither ever aborts the run anymore - both
            // are surfaced via this warning and via EmitValidationTelemetry/the written
            // report, and the run always proceeds to indexing.
            //
            // The message reports both counts rather than just ReconciliationProblems.Count -
            // a run can fail with zero reconciliation problems purely on error rate (e.g. a
            // burst of TextQuality corruption errors), in which case a reconciliation-only
            // message would read "(0 reconciliation problem(s))" and give no hint what
            // actually tripped the evaluation.
            if (!validation.Passed)
            {
                var errorIssueCount = validation.Issues.Count(i => i.IsError);

                _logger.LogWarning(
                    "PDF validation failed ({Reconciliation} reconciliation problem(s), {Errors} error-severity issue(s)) " +
                    "— continuing (validation no longer aborts a run).",
                    validation.ReconciliationProblems.Count, errorIssueCount);
            }

            // Becomes the new magnitude-check baseline whether the gate passed outright or
            // only passed because we're in Development — same reasoning as CSV's "whether
            // passed normally or via override" comment: the alternative (never saving on a
            // Development continue) would leave the baseline stuck at whatever the last
            // Production run saw, permanently mis-sizing every subsequent magnitude check.
            await SaveRunStateAsync(cleanResult.Records.Count, previousETag, ct);

            var (errors, warnings, missingTitle) = CountIssues(validation, cleanResult);
            return BuildExtractionOutput(fileResults, validation, cleanResult, errors, warnings, missingTitle, lastModifiedByBlob, zenyaByBlob);
        }
        catch (Exception ex)
        {
            // Captured (not swallowed - rethrown below) so the finally block can still
            // write *something* even when the run failed before a PdfQualityGateResult
            // ever existed (blob listing, cleaning) - see the WriteFailureReportAsync
            // branch below.
            failure = ex;
            throw;
        }
        finally
        {
            if (validation is not null)
            {
                // Each caught independently: a failure in one (e.g. a transient blob error
                // writing the report) must not mask whatever real exception the try block
                // is already propagating (including the reconciliation-gate InvalidOperationException
                // above), and must not stop the other of the two from still running.
                try
                {
                    // always send telemetry
                    EmitValidationTelemetry(validation, cleanResult!, fileResults!);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to emit PDF validation telemetry for run at {RunAt}.", runAt);
                }

                try
                {
                    // must always run - fileResults is never null here: it's assigned in
                    // step 1, before validation (step 4) can be assigned at all.
                    await WriteReportsAsync(runAt, instanceId, validation, fileResults!, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to write PDF extraction reports for run at {RunAt}.", runAt);
                }
            }
            else if (failure is not null)
            {
                // Nothing made it as far as a PdfQualityGateResult (blob listing or cleaning
                // threw) - write a minimal failure report instead so this run still leaves
                // something behind, same CancellationToken.None reasoning as
                // WriteReportsAsync above.
                try
                {
                    await WriteFailureReportAsync(runAt, instanceId, failure, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to write PDF extraction failure report for run at {RunAt}.", runAt);
                }
            }
        }
    }

    // Downloads and extracts every blob in sourceIdsToProcess, up to MaxExtractionParallelism at a
    // time. One file's exception (network blip, an unexpected extractor bug) shouldn't abort
    // the whole run — it becomes a file-level PipelineIssue instead, same treatment
    // TryOpenAndValidate already gives a corrupt PDF. sourceIdsToProcess already carries each
    // blob's LastModified/ContentLength/Zenya metadata from ExtractionService's own
    // pre-extraction listing/diff, so there's no need to list the container again here —
    // just download and extract whatever's in the set.
    // Below host.json's durableTask.activityFunctionTimeout (60 minutes, see
    // AnalyzeBudgetCeiling's own comment for why matching that ceiling matters) - a fixed
    // margin under it so a file that's already mid-download/mid-analyze when the corpus
    // wall clock is checked still has room to finish before Durable's own timeout would
    // redeliver and re-bill the whole activity.
    private static readonly TimeSpan CorpusWallClockLimit = TimeSpan.FromMinutes(50);

    private async Task<(List<PdfExtractionResult> Results, PdfCleanResult CleanResult, Dictionary<string, DateTimeOffset> LastModified, Dictionary<string, ZenyaMetadata> Zenya)> ExtractPdfsFromBlobAsync(
        IReadOnlyDictionary<string, PdfBlobInfo> sourceIdsToProcess, DateTimeOffset runAt, CancellationToken ct)
    {
        // Declares thread-safe collections:
        // One to accumulate per-blob extraction results => ConcurrentBag<T> is a thread-safe, unordered collection, multiple threads can call .Add() on it at once without locking
        var results      = new ConcurrentBag<PdfExtractionResult>();
        // One PdfCleanResult per successfully-extracted file (finding #14: cleaning starts
        // the moment that file's own extraction finishes, not after the whole corpus does).
        // Each is built by its own call to _pdfCleaner.CleanPdf, which only ever mutates the
        // one PdfCleanResult it just created - safe to Add here without further locking, same
        // as results above. Merged into one run-level PdfCleanResult after the loop.
        var cleanResults = new ConcurrentBag<PdfCleanResult>();
        // Ordinal, not OrdinalIgnoreCase: Azure blob names are case-sensitive, so "Beleid.pdf"
        // and "beleid.pdf" are two different blobs that can legally sit in the same container.
        // Under a case-insensitive comparer these indexers silently overwrite one with the
        // other's metadata, and the ToDictionary calls in BuildDocuments below throw outright
        // on the same pair - after every paid Document Intelligence call in the run has been
        // made. Every blob-name-keyed collection in this pipeline is Ordinal for that reason.
        var lastModified = new ConcurrentDictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var zenya        = new ConcurrentDictionary<string, ZenyaMetadata>(StringComparer.Ordinal);

        // C7 measurement, always populated - see the LogContentHashOutcome call below.
        var contentHashes = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        // Iterates through the entries to process, for each one runs the download-and-extract
        await Parallel.ForEachAsync(
            sourceIdsToProcess,
            new ParallelOptions { MaxDegreeOfParallelism = MaxExtractionParallelism, CancellationToken = ct },
            async (pair, cancellationToken) =>
            {
                var (name, entry) = pair;

                lastModified[name] = entry.LastModified;
                zenya[name]        = entry.Zenya;

                // Try block covers the download too: a failed download for one blob must not
                // abort the run - and under Parallel.ForEachAsync an uncaught exception would
                // also cancel the other in-flight tasks, discarding paid DI calls mid-flight.
                try
                {
                    // Corpus-level wall-clock guard (finding #11): a partial run that stops
                    // submitting new files here completes cleanly well inside Durable's
                    // activityFunctionTimeout; a run that keeps submitting until that
                    // timeout fires gets the WHOLE activity redelivered, re-billing every
                    // already-completed analysis in this run, not just whichever file was
                    // still in flight. Checked per file (not just once) since this loop runs
                    // MaxExtractionParallelism-wide and stays open for the whole corpus.
                    //
                    // Deliberately not recorded as a PdfExtractionResult/PipelineIssue: this
                    // is an intentional, graceful stopping point, not a defect - folding it
                    // into the error-rate gate would mean a large first-time corpus run could
                    // fail validation (and abort the whole run) purely because it correctly
                    // stopped early. Simply not extracting this file this run is enough: the
                    // pre-extraction diff never advances its indexed date, so it's picked up
                    // as new/updated again on the very next run.
                    if (DateTimeOffset.UtcNow - runAt > _corpusWallClockLimit)
                    {
                        _logger.LogWarning(
                            "'{Blob}' not submitted - corpus wall-clock limit ({Limit}) reached; stopping new submissions this run so the activity completes cleanly. Will be picked up on the next run.",
                            name, _corpusWallClockLimit);
                        return;
                    }

                    // ContentLength already came from the cheap blob listing (ExtractionService's
                    // own pre-extraction diff) - rejecting an over-limit file here costs nothing,
                    // versus downloading up to hundreds of MB just to have
                    // PdfDocumentValidator.IsPDFSizeOkForDI reject it after the fact. Only acts
                    // when the length is known; a null ContentLength falls through to the normal
                    // download-then-validate path, same as today.
                    if (entry.ContentLength is { } contentLength && contentLength > PdfDocumentValidator.MaxBytes)
                    {
                        results.Add(new PdfExtractionResult(false, name, contentLength, null, null, null, null, null, null,
                            PipelineIssue.Error(
                                PipelineStage.ParsePages,
                                name,
                                $"File is {contentLength / 1024.0 / 1024.0:F1} MB, exceeds the {PdfDocumentValidator.MaxBytes / 1024 / 1024} MB Document Intelligence limit.",
                                reason: PdfOpenFailureReason.TooLarge)));
                        return;
                    }

                    var pdfBytes = await _blobStore.DownloadBytesAsync(_container, name, cancellationToken);

                    // C7 (pre-chunking-action-items.md), measurement half only. Computed here,
                    // before any backend is invoked, so it applies regardless of which
                    // IPdfExtractor ends up running - same bytes always hash the same,
                    // independent of blob name. No longer gated on Debug logging: this is a
                    // reported metric now, not just a log line, and SHA-256 over bytes already
                    // in memory is negligible next to the paid call that follows.
                    contentHashes[name] = ComputeContentHash(pdfBytes);

                    var extracted = await _extractor.ExtractPDFAsync(name, pdfBytes, cancellationToken);
                    results.Add(extracted);

                    // Clean this file's pages now, while the rest of the corpus is still
                    // being extracted (finding #14) - instead of flattening every file's
                    // pages into one list and cleaning it only after the whole
                    // Parallel.ForEachAsync loop below has finished.
                    if (extracted.Ok)
                        cleanResults.Add(_pdfCleaner.CleanPdf(extracted.Pages!));
                }
                catch (OperationCanceledException)
                {
                    throw; // real cancellation should still stop the run, not log as a file error
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Download or extraction failed for '{Blob}'; recording as a file-level error.", name);
                    results.Add(new PdfExtractionResult(false, name, entry.ContentLength ?? 0, null, null, null, null, null, null,
                        PipelineIssue.Error(PipelineStage.ParsePages, name, ex.Message, reason: PdfOpenFailureReason.Unknown)));
                }
            });

        LogContentHashOutcome(contentHashes, results);

        var cleanResult = new PdfCleanResult();
        foreach (var perFile in cleanResults)
            cleanResult.MergeFrom(perFile);

        return (results.ToList(),
            cleanResult,
            new Dictionary<string, DateTimeOffset>(lastModified, StringComparer.Ordinal),
            new Dictionary<string, ZenyaMetadata>(zenya, StringComparer.Ordinal));
    }

    // The validation report (same shape CsvExtractionOrchestrator writes) plus a second,
    // PDF-only report of what each extraction step actually produced per file.
    //
    // Written in every environment - IRunReportWriter.IsEnabled is unconditionally true now,
    // on the principle that the one environment where you can't attach a debugger shouldn't
    // also be the one with no reports. The guard is kept as the single place that decides.
    private async Task WriteReportsAsync(
        DateTimeOffset runAt, string? instanceId, PdfQualityGateResult report,
        IReadOnlyList<PdfExtractionResult> fileResults, CancellationToken ct)
    {
        if (!_reportWriter.IsEnabled) return;

        await _reportWriter.WriteReportAsync(
            StageReportPath.Build(ValidationReportName, runAt, instanceId), report, ct);

        // PdfPig facts already read off each file's PdfDocument before it was disposed
        // (FileSizeBytes/PdfSpecVersion from PdfDocumentValidator, NativeMetadata from
        // PdfNativeMetadataExtractor) - none of it recomputed here, just the fields that
        // otherwise get silently dropped (PdfExtractionDocument only carries a subset of
        // NativeMetadata onward; Producer/Creator/Subject/Keywords never reach any report
        // today). Useful for corpus-level QA: spec-version/size distribution across a run,
        // and flagging docs with no Producer/Creator as a non-standard export path.
        var fileFacts = fileResults.Select(f => new
        {
            f.BlobName,
            f.Ok,
            f.FileSizeBytes,
            f.PdfSpecVersion,
            f.NativeMetadata,
            f.EstimatedCostUsd,
        }).ToList();

        await _reportWriter.WriteReportAsync(
            StageReportPath.Build(FileFactsReportName, runAt, instanceId), fileFacts, ct);
    }

    private sealed record PdfExtractionFailureReport(
        DateTimeOffset RunAt, string ExceptionType, string Message, string? StackTrace);

    // Same write-everywhere note as WriteReportsAsync above - fallback for a run that failed
    // before a PdfQualityGateResult ever existed (blob listing or cleaning threw), so there's
    // still something written for that run instead of silence.
    private async Task WriteFailureReportAsync(DateTimeOffset runAt, string? instanceId, Exception failure, CancellationToken ct)
    {
        if (!_reportWriter.IsEnabled) return;

        await _reportWriter.WriteReportAsync(
            StageReportPath.Build(FailureReportName, runAt, instanceId),
            new PdfExtractionFailureReport(runAt, failure.GetType().FullName ?? failure.GetType().Name, failure.Message, failure.StackTrace),
            ct);
    }

    // Maps the validated, cleaned records into the source-agnostic PdfExtractionOutput
    // returned to the caller. Several PdfExtractionOutput fields have no PDF equivalent and
    // are always null here (not "verified zero") — see PdfQualityGateResult's own comment:
    // no Zenya attention-flag (StaleDocCount), no version data (MissingVersionCount -
    // nothing parses/populates Version for PDFs), and no folder/department concept
    // (MissingDepartmentCount).
    //
    // fileResults (chunking-rewrite-plan.md item #1) feeds the lookups below (items #2/#3)
    // so real section breadcrumbs / DI structure / native metadata reach PdfExtractionDocument
    // as typed fields, instead of being discarded after validation reads Structure for its
    // own checks. FileSizeBytes/PdfSpecVersion/EstimatedCostUsd and PageErrors/Warnings are
    // deliberately not threaded through - see PdfExtractionDocument's own comment for why.
    private static PdfExtractionOutput BuildExtractionOutput(
        IReadOnlyList<PdfExtractionResult> fileResults,
        PdfQualityGateResult                report,
        PdfCleanResult                      cleanResult,
        int                                 errors,
        int                                 warnings,
        int                                 missingTitle,
        Dictionary<string, DateTimeOffset>  lastModifiedByBlob,
        Dictionary<string, ZenyaMetadata>   zenyaByBlob)
    {
        var extractionDocs = BuildDocuments(fileResults, cleanResult, lastModifiedByBlob, zenyaByBlob);

        // No projection needed: report.Issues is already the type the output carries.
        // This used to convert ValidationIssue -> ValidationIssueEntry field by field,
        // purely to cross an assembly boundary, and needed a null-forgiving ! on
        // DocumentId because the two types disagreed about its nullability.
        var issues = report.Issues
            .Take(MaxReturnedIssues)
            .ToList();

        var spotCheck = report.SpotCheckSample
            .Select(r => new SpotCheckEntry(
                r.BlobName,
                r.Title,
                r.PageContent.Length > 300 ? r.PageContent[..300] + "…" : r.PageContent))
            .ToList();

        // Zenya metadata is file-level (same lookup used above, per BlobName) - a document
        // only counts as missing if the field genuinely isn't set, not per-page like
        // missingTitle above (which reads a per-page column on cleanResult.Records).
        var okBlobNames = fileResults.Where(f => f.Ok).Select(f => f.BlobName)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var traceabilityGapCount = okBlobNames.Count(b => (zenyaByBlob.GetValueOrDefault(b) ?? ZenyaMetadata.Empty).DocumentId is null);
        var missingVersionCount  = okBlobNames.Count(b => (zenyaByBlob.GetValueOrDefault(b) ?? ZenyaMetadata.Empty).Version is null);

        var redFlags = report.RedFlags.ToList();
        if (traceabilityGapCount > 0)
            redFlags.Add(
                $"{traceabilityGapCount} document(s) have no zenya_document_id blob metadata set — " +
                "citations built from these will show a traceability gap (Citation.TraceabilityGap).");

        return new PdfExtractionOutput(extractionDocs)
        {
            ValidationErrors       = errors,
            ValidationWarnings     = warnings,
            ReconciliationProblems = report.ReconciliationProblems.Count,
            StaleDocCount          = null,  // no Zenya attention-flag equivalent for PDFs
            MojibakeRepairedPages  = report.MojibakeRepairedPages,
            DetectedTableCount     = report.DetectedTableCount,
            DocsWithoutHeadings    = report.DocumentsNeedingFallbackChunking.Count,
            MissingTitleCount      = missingTitle,
            MissingVersionCount    = missingVersionCount,
            MissingDepartmentCount = null,  // no folder/department concept for PDFs
            TraceabilityGapCount   = traceabilityGapCount,
            Issues                 = issues,
            RedFlags               = redFlags,
            SpotCheckSample        = spotCheck,
        };
    }

    // The separator between two pages' cleaned text in the assembled document. A blank line
    // is what every downstream splitter already treats as a paragraph boundary
    // (PdfChunkingStrategy1 splits on "\n\n"), so joining with anything else would invent a
    // boundary shape nothing else recognises. Its length is accounted for in PageSpans, so
    // the offsets stay exact.
    //
    // Assembly is the only stage that runs after cleaning, which makes it the only place a
    // per-page cleaning invariant can be broken again: PdfCleaner collapses \n{3,} down to one
    // blank line within a page, and nothing re-collapses the joined text. So every invariant
    // cleaning established has to be re-established at the join - see the blank-page case in
    // BuildDocuments, where appending this on both sides of an empty page would produce exactly
    // the four-newline run PdfCleaner exists to prevent.
    private const string PageSeparator = "\n\n";

    // Assembles one PdfExtractionDocument per PDF from its cleaned pages (action-plan.md C8).
    //
    // Pages are cleaned individually upstream and joined here, recording each page's offset
    // as it is appended. That ordering matters: cleaning per page keeps one bad page from
    // failing the file, and recording offsets during assembly means the page map is exact
    // rather than a downstream guess at the separator.
    //
    // A document with no surviving cleaned pages produces no record at all, rather than an
    // empty-content one - "extraction produced nothing for this file" is a real outcome the
    // validation report already covers, and an empty document would chunk to zero chunks
    // while looking like a successfully processed file.
    internal static List<PdfExtractionDocument> BuildDocuments(
        IReadOnlyList<PdfExtractionResult>  fileResults,
        PdfCleanResult                      cleanResult,
        Dictionary<string, DateTimeOffset>  lastModifiedByBlob,
        Dictionary<string, ZenyaMetadata>   zenyaByBlob)
    {
        var nativeMetadataByBlob = BuildNativeMetadataLookup(fileResults);
        var resultByBlob         = fileResults
            .Where(f => f.Ok)
            .ToDictionary(f => f.BlobName, f => f, StringComparer.Ordinal);

        var documents = new List<PdfExtractionDocument>();

        // cleanResult.Records is merged from a ConcurrentBag, so its order is nondeterministic
        // across runs. Grouping, then ordering pages by PageNumber and documents by blob name,
        // is what makes the output stable - and that matters beyond tidiness: ChunkingHelper.SafeKey
        // derives chunk ids from SourceId plus the chunk's index within the document, so an
        // unstable assembly order would reshuffle every chunk id from one run to the next.
        // (The ordering is only total because PdfPipelineValidator separately asserts no
        // duplicate (BlobName, PageNumber) - with a duplicate, OrderBy's stability would
        // tie-break on bag order and the ids would drift again.)
        foreach (var group in cleanResult.Records
                     .GroupBy(r => r.BlobName, StringComparer.Ordinal)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var pages = group.OrderBy(r => r.PageNumber).ToList();

            // Always hits: cleanResult only ever receives records from a file whose extraction
            // succeeded (see ExtractPdfsFromBlobAsync's `if (extracted.Ok)` before CleanPdf), so
            // every blob that reaches this loop has an Ok result. The file?. guards below are
            // defence against that invariant being broken later, not a reachable state - a
            // document with null Profile/Language and empty structure is not something this
            // pipeline can currently produce.
            resultByBlob.TryGetValue(group.Key, out var file);
            var nativeMetadata = nativeMetadataByBlob.GetValueOrDefault(group.Key);
            var zenya          = zenyaByBlob.GetValueOrDefault(group.Key) ?? ZenyaMetadata.Empty;
            var structure      = file?.Structure;

            // DistinctBy before ToDictionary: DI is expected to report each page once, but a
            // duplicate PageNumber would otherwise throw here and take down a run that has
            // already paid for every analysis in it. First entry wins.
            var dimensionsByPage = (structure?.PageDimensions ?? [])
                .DistinctBy(d => d.PageNumber)
                .ToDictionary(d => d.PageNumber, d => d);

            // Presized: without it the builder regrows and copies its way up to what can be a
            // whole large PDF's text. Separator count is an upper bound (blank pages don't get
            // one - see below), which is the right side to err on for a capacity hint.
            var content   = new StringBuilder(
                pages.Sum(p => p.PageContent.Length) + (pages.Count - 1) * PageSeparator.Length);
            var pageSpans = new List<PageSpan>(pages.Count);

            foreach (var page in pages)
            {
                // No separator around a page that cleaned to nothing - a caption-less diagram
                // page does exactly that (PdfCleaner.ConvertFigures drops the placeholder), and
                // it is kept as a record rather than dropped. Separating on both sides of it
                // would leave a four-newline run in the assembled text: PdfCleaner collapses
                // \n{3,} per page, but assembly runs after cleaning and nothing re-collapses
                // across the join, so the join is the one place such a run can reach the index.
                //
                // The page still gets a zero-length span. Dropping it would drop its
                // IsPictureOnly flag, which is the only signal that a mostly-normal document
                // has diagram pages in it (see PageSpan) - the document-level density gate
                // passes such a file comfortably.
                if (content.Length > 0 && page.PageContent.Length > 0) content.Append(PageSeparator);

                pageSpans.Add(new PageSpan(
                    PageNumber:    page.PageNumber,
                    Offset:        content.Length,
                    Length:        page.PageContent.Length,
                    Dimensions:    dimensionsByPage.GetValueOrDefault(page.PageNumber),
                    IsPictureOnly: page.IsPictureOnlyPage));

                content.Append(page.PageContent);
            }

            documents.Add(new PdfExtractionDocument(
                SourceId:         group.Key,
                Content:          content.ToString(),
                PageSpans:        pageSpans,
                // Read from the file's own metadata, not off pages[0]. The title is already a
                // file-level fact - GetTitleHelper resolves it once per file (native Title, else
                // a filename-derived fallback) and GetPagesHelper stamps the same value on every
                // page - so reading it back off a page was the last place assembly still depended
                // on the per-page shape. Same value either way; this one can't be wrong if a
                // document's first page is ever missing.
                Title:            GetTitleHelper.GetTitle(nativeMetadata, group.Key),
                Author:           nativeMetadata?.Author,
                CreatedAt:        nativeMetadata?.CreatedAt,
                ModDate:          nativeMetadata?.ModDate,
                PageCount:        nativeMetadata?.PageCount,
                LastModifiedDate: lastModifiedByBlob.TryGetValue(group.Key, out var lm) ? lm : null,
                ZenyaDocumentId:  zenya.DocumentId,
                ZenyaVersion:     zenya.Version,
                ZenyaStatus:      zenya.Status,
                ZenyaUrl:         zenya.Url,
                Bookmarks:        nativeMetadata?.Bookmarks ?? [],
                PageBreadcrumbs:  file?.SectionBreadcrumbs ?? new Dictionary<int, string>(),
                Sections:         structure?.Sections       ?? [],
                Headings:         structure?.Headings       ?? [],
                Boilerplate:      structure?.Boilerplate    ?? [],
                Tables:           structure?.Tables         ?? [],
                SelectionMarks:   structure?.SelectionMarks ?? [],
                Figures:          structure?.Figures        ?? [],
                Lines:            structure?.Lines          ?? [],
                Profile:          file?.Profile,
                Language:         file?.Language));
        }

        return documents;
    }

    // File-level native PDF facts (Author, CreatedAt, PageCount, Bookmarks) - one entry per
    // blob, read once per document now rather than re-attached to every page. Ordinal for the
    // same reason as every other blob-name-keyed collection here - see ExtractPdfsFromBlobAsync.
    private static Dictionary<string, DocMetadata> BuildNativeMetadataLookup(
        IReadOnlyList<PdfExtractionResult> fileResults) =>
        fileResults
            .Where(f => f.Ok && f.NativeMetadata is not null)
            .ToDictionary(f => f.BlobName, f => f.NativeMetadata!, StringComparer.Ordinal);


    // Pure counts derived from the report/cleanResult — no side effects, safe to compute
    // independently of whether EmitValidationTelemetry below ever runs. BuildExtractionOutput
    // needs these on the success path; EmitValidationTelemetry recomputes them itself so
    // its own (always-run, finally-block) emission doesn't depend on the success path
    // having reached this call.
    private static (int Errors, int Warnings, int MissingTitle) CountIssues(
        PdfQualityGateResult report, PdfCleanResult cleanResult)
    {
        var errors   = report.Issues.Count(i => i.IsError);
        var warnings = report.Issues.Count(i => i.IsWarning);

        // Metadata completeness — a document only counts as "missing" if EVERY one of
        // its pages lacks that field, matching CsvExtractionOrchestrator's rule.
        var byDocument   = cleanResult.Records.GroupBy(r => r.BlobName).ToList();
        var missingTitle = byDocument.Count(g => g.All(r => string.IsNullOrWhiteSpace(r.Title)));

        return (errors, warnings, missingTitle);
    }

    // Everything this run logs and emits as metrics, in one place. Always called from
    // ExtractDocumentsAsync's finally block once a report exists, independent of whether
    // validation passed - report.Passed already reflects that (a failed run is still
    // logged and recorded as failed here, not silently dropped because the gate's throw
    // already unwound the try block).
    private void EmitValidationTelemetry(
        PdfQualityGateResult report, PdfCleanResult cleanResult, IReadOnlyList<PdfExtractionResult> fileResults)
    {
        foreach (var warning in report.MagnitudeWarnings)
            _logger.LogWarning("{Warning}", warning);

        _logger.LogInformation("PDF validation {Result} — {Cleaned} records, {Issues} issues",
            report.Passed ? "passed" : "failed", report.CleanedRecords, report.Issues.Count);

        _logger.LogInformation(
            "PDF cleaning this run: {Mojibake} mojibake page(s), {ControlChars} control char(s), " +
            "{InvisibleChars} invisible char(s), {Ligatures} ligature(s), {HyphenJoins} hyphenation join(s), " +
            "{LineWraps} line wrap(s) reflowed — " +
            "all zero means the source had nothing to clean, not that cleaning didn't run.",
            report.MojibakeRepairedPages, report.ControlCharsStripped, report.InvisibleCharsStripped,
            report.LigaturesExpanded, report.HyphenationJoinsRepaired, report.LineWrapsReflowed);

        foreach (var issue in report.Issues.Take(MaxLoggedIssues))
            _logger.Log(
                issue.IsError ? LogLevel.Error : LogLevel.Warning,
                "[{Stage}] {DocId}: {Message}", issue.Stage, issue.DocumentId, issue.Message);
        if (report.Issues.Count > MaxLoggedIssues)
            _logger.LogWarning("…{More} more issue(s) not logged (see the run report for the full list).",
                report.Issues.Count - MaxLoggedIssues);

        var (errors, warnings, missingTitle) = CountIssues(report, cleanResult);

        var sourceTag = new KeyValuePair<string, object?>("source", Source);

        Instrumentation.ValidationIssues.Add(errors,   sourceTag, new("severity", "error"));
        Instrumentation.ValidationIssues.Add(warnings, sourceTag, new("severity", "warning"));
        Instrumentation.DocsWithoutHeadings.Add(report.DocumentsNeedingFallbackChunking.Count, sourceTag);
        Instrumentation.MojibakeRepairedPages.Add(report.MojibakeRepairedPages, sourceTag);
        Instrumentation.DetectedTableCount.Record(report.DetectedTableCount, sourceTag);

        Instrumentation.MissingMetadata.Add(missingTitle, sourceTag, new("field", "title"));

        var estimatedCostUsd = fileResults.Sum(f => f.EstimatedCostUsd ?? 0m);
        if (estimatedCostUsd > 0)
        {
            _logger.LogInformation("PDF extraction estimated cost this run: ${Cost:F2}", estimatedCostUsd);
            Instrumentation.DiEstimatedCostUsd.Add((double)estimatedCostUsd, sourceTag);
        }
    }

    // Stable hash of the PDF's raw bytes (C7):
    // - Same file content -> same hash, regardless of the blob's file name.
    // - Two uses today, both measurement: the duplicate-detection signal below, and a
    //   rename-proof document identity (F1 - the corpus has no Zenya document ids, so the
    //   blob name is otherwise the only identifier, and a rename silently creates a "new"
    //   document).
    private static string ComputeContentHash(byte[] pdfBytes) =>
        Convert.ToHexString(SHA256.HashData(pdfBytes));

    // C7, measurement only. A content-hash-keyed cache of extraction *results* was built and
    // then deliberately removed: keying on the build id (so a code change can't serve stale
    // extractions) excludes the one case such a cache would pay for - a full reindex after a
    // code change - leaving only "blob touched but bytes unchanged" and duplicate uploads.
    // It also could not dedup duplicates within a single run, since parallel workers hash and
    // miss before either write lands, and it saved only the Document Intelligence call, never
    // the download that precedes the hash.
    //
    // So this logs the evidence instead of assuming the answer: run it for a while, read
    // Distinct vs Total, and build the cache only if the numbers justify it.
    private void LogContentHashOutcome(
        IReadOnlyDictionary<string, string> contentHashes,
        IEnumerable<PdfExtractionResult> results)
    {
        if (contentHashes.Count == 0) return;

        var distinctHashes = contentHashes.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var billedUsd      = results.Sum(r => r.EstimatedCostUsd ?? 0m);

        _logger.LogInformation(
            "Content hashes (build {Version}): {Total} document(s) extracted, {Distinct} distinct by bytes, ${Billed:F2} billed this run.",
            ExtractionVersion.AssemblyVersion, contentHashes.Count, distinctHashes, billedUsd);

        // Two blobs with the same hash are the same file uploaded twice - a corpus-hygiene
        // finding that stands on its own, independent of whether anything ever caches on it.
        if (distinctHashes < contentHashes.Count)
        {
            var duplicateGroups = contentHashes
                .GroupBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => string.Join(" == ", g.Select(kv => kv.Key)));

            _logger.LogWarning(
                "Byte-identical duplicates in the corpus ({Total} document(s), {Distinct} distinct): {Groups}",
                contentHashes.Count, distinctHashes, string.Join(" | ", duplicateGroups));
        }
    }

    private async Task<(int? Count, ETag? ETag)> PreviousRunCount(CancellationToken ct)
    {
        var (state, etag) = await _blobStore.TryReadJsonWithETagAsync<RunState>(_stateContainer, StateBlobName, ct);
        return (state?.CleanedRecords, etag);
    }

    private Task SaveRunStateAsync(int cleanedRecords, ETag? previousETag, CancellationToken ct) =>
        _blobStore.SaveJsonWithETagAsync(_stateContainer, StateBlobName, new RunState(cleanedRecords), previousETag, ct);
}
