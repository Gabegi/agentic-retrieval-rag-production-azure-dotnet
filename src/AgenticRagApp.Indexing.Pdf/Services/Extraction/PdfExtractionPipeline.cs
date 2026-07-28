using System.Collections.Concurrent;
using System.Security.Cryptography;
using Azure;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AgenticRagApp.Infrastructure.Clients.Blob;
using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Common.Models;
using AgenticRagApp.Observability;
using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Indexing.Pdf.Services;

// PDF implementation of IExtractionOrchestrator — mirrors CsvExtractionOrchestrator's
// shape (download -> extract -> clean -> validate -> report -> diff-ready output),
// adapted to the PDF pipeline. So each file's PDFExtractionResult.Diagnostics
// (currently always null - nothing populates it since the PdfPig backend was removed)
// has somewhere to be written as a dev-only report. Extractors stay I/O-free;
// orchestrators own reporting — same split ExtractionService/CsvExtractionOrchestrator
// already follow for IRunReportWriter.
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

    public string Source => "pdf";

    private const string StateBlobName = "pdf-extraction-state.json";

    // Folder segment namespacing every report blob this orchestrator writes, so it
    // doesn't mix into CsvExtractionOrchestrator's blobs in the same "pipeline-reports" container.
    private const string ReportFolder = "indexing/pdf-extraction";

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

    private sealed record RunState(int CleanedRecords);

    public PdfExtractionPipeline(
        BlobContainerClient                container,
        BlobContainerClient                stateContainer,
        IBlobStore                         blobStore,
        IRunReportWriter                   reportWriter,
        IPdfExtractor                      extractor,
        IPdfCleaner                        cleaner,
        IPdfPipelineValidator              validator,
        IHostEnvironment                   env,
        ILogger<PdfExtractionPipeline> logger)
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
    }

    public async Task<PdfExtractionOutput> ExtractDocumentsAsync(
        IReadOnlyDictionary<string, PdfBlobInfo> sourceIdsToProcess, CancellationToken ct = default)
    {
        var runAt = DateTimeOffset.UtcNow;

        // Captured as the pipeline progresses so the finally block below can always emit
        // telemetry and write a report off whatever got built, regardless of where the try
        // block throws (the reconciliation-gate abort, SaveRunStateAsync, BuildExtractionOutput).
        // Nothing to emit if Validate itself never ran (blob listing/cleaning/PreviousRunCount
        // failed) - there's no report or cleanResult to build either from.
        List<PdfExtractionDiagnostics> diagnostics = new();
        PdfValidationReport?           validation  = null;
        PdfCleanResult?                cleanResult = null;
        List<PDFExtractionResult>?     fileResults = null;
        Exception?                     failure     = null;

        try
        {
            // 1/ Extract Data from PDFs
            Dictionary<string, DateTimeOffset> lastModifiedByBlob;
            Dictionary<string, ZenyaMetadata>  zenyaByBlob;
            (fileResults, lastModifiedByBlob, zenyaByBlob) = await ExtractPdfsFromBlobAsync(sourceIdsToProcess, ct);


            // 2/ Clean pages

            // Filters to successfully-extracted files and flattens their pages into one list
            // because _cleaner.Clean only needs page content — not the file-level error/warning data that _validator.Validate needs separately from fileResults itself
            var pages = fileResults.Where(f => f.Ok).SelectMany(f => f.Pages!).ToList();
            cleanResult = _pdfCleaner.CleanPdf(pages);

            // 3/ Check # of docs processed vs previous run
            var (previousCount, previousETag) = await PreviousRunCount(ct);
            diagnostics = fileResults.Select(f => f.Diagnostics).OfType<PdfExtractionDiagnostics>().ToList();

            // 4/ Validate results
            validation = _validator.Validate(fileResults, cleanResult, previousCount, diagnostics);

            // 5/ Validation Gate — reconciliation problems only (magnitude-shift is
            // advisory-only, see PdfPipelineValidator's tiering comment; report.Passed
            // already excludes it). Only enforced outside Development, so local/dev runs
            // aren't blocked by reconciliation noise while experimenting. Still surfaced as
            // a warning either way via EmitValidationTelemetry.
            if (!validation.Passed)
            {
                if (_env.IsDevelopment())
                    _logger.LogWarning(
                        "PDF validation failed ({Reconciliation} reconciliation problem(s)) " +
                        "— continuing because we're in Development.",
                        validation.ReconciliationProblems.Count);
                else
                    throw new InvalidOperationException(
                        $"PDF validation failed ({validation.ReconciliationProblems.Count} reconciliation problem(s)) " +
                        "— aborting extraction.");
            }

            await SaveRunStateAsync(cleanResult.Records.Count, previousETag, ct);

            var (errors, warnings, missingTitle) = CountIssues(validation, cleanResult);
            return BuildExtractionOutput(fileResults, validation, cleanResult, errors, warnings, missingTitle, lastModifiedByBlob, zenyaByBlob);
        }
        catch (Exception ex)
        {
            // Captured (not swallowed - rethrown below) so the finally block can still
            // write *something* even when the run failed before a PdfValidationReport
            // ever existed (blob listing, cleaning, PreviousRunCount) - see the
            // WriteFailureReportAsync branch below.
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
                    EmitValidationTelemetry(validation, cleanResult!);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to emit PDF validation telemetry for run at {RunAt}.", runAt);
                }

                try
                {
                    // must always run - fileResults is never null here: it's assigned in
                    // step 1, before validation (step 4) can be assigned at all.
                    await WriteReportsAsync(runAt, validation, diagnostics, fileResults!, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to write PDF extraction reports for run at {RunAt}.", runAt);
                }
            }
            else if (failure is not null)
            {
                // Nothing made it as far as a PdfValidationReport (blob listing, cleaning,
                // or PreviousRunCount threw) - write a minimal failure report instead so
                // this run still leaves something behind, same CancellationToken.None
                // reasoning as WriteReportsAsync above.
                try
                {
                    await WriteFailureReportAsync(runAt, failure, CancellationToken.None);
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
    // the whole run — it becomes a file-level ExtractionError instead, same treatment
    // TryOpenAndValidate already gives a corrupt PDF. sourceIdsToProcess already carries each
    // blob's LastModified/ContentLength/Zenya metadata from ExtractionService's own
    // pre-extraction listing/diff, so there's no need to list the container again here —
    // just download and extract whatever's in the set.
    private async Task<(List<PDFExtractionResult> Results, Dictionary<string, DateTimeOffset> LastModified, Dictionary<string, ZenyaMetadata> Zenya)> ExtractPdfsFromBlobAsync(
        IReadOnlyDictionary<string, PdfBlobInfo> sourceIdsToProcess, CancellationToken ct)
    {
        // Declares thread-safe collections:
        // One to accumulate per-blob extraction results => ConcurrentBag<T> is a thread-safe, unordered collection, multiple threads can call .Add() on it at once without locking
        var results      = new ConcurrentBag<PDFExtractionResult>();
        var lastModified = new ConcurrentDictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        var zenya        = new ConcurrentDictionary<string, ZenyaMetadata>(StringComparer.OrdinalIgnoreCase);

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
                    var pdfBytes = await _blobStore.DownloadBytesAsync(_container, name, cancellationToken);

                    // Computed here, before any backend is invoked, so it applies regardless of
                    // which IPdfExtractor ends up running - same bytes always hash the same,
                    // independent of blob name, so a byte-identical re-upload is detectable before
                    // paying for extraction again. Not yet compared against anything (no store of
                    // previously-seen hashes exists) - logged for now so the value is at least
                    // visible while that dedup check gets built. Gated on IsEnabled so SHA-256 isn't
                    // computed on every blob, every run, when Debug logging is off (the normal case).
                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug("'{Blob}' content hash: {Hash}", name, ComputeContentHash(pdfBytes));

                    results.Add(await _extractor.ExtractPDFAsync(name, pdfBytes, cancellationToken));
                }
                catch (OperationCanceledException)
                {
                    throw; // real cancellation should still stop the run, not log as a file error
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Download or extraction failed for '{Blob}'; recording as a file-level error.", name);
                    results.Add(new PDFExtractionResult(false, name, entry.ContentLength ?? 0, null, null, null, null, null, null,
                        new ExtractionError(RowNumber: 0, DocumentId: name, Message: ex.Message, Reason: PdfOpenFailureReason.Unknown)));
                }
            });

        return (results.ToList(),
            new Dictionary<string, DateTimeOffset>(lastModified, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, ZenyaMetadata>(zenya, StringComparer.OrdinalIgnoreCase));
    }

    // Dev-only (see IRunReportWriter.IsEnabled): the validation report (same shape
    // CsvExtractionOrchestrator writes) plus a second, PDF-only report of what each
    // extraction step actually produced per file. Currently always empty - diagnostics
    // is only ever populated by the PdfPig backend, which has been removed; left in place
    // as a report slot for whichever backend picks that reporting back up.
    private async Task WriteReportsAsync(
        DateTimeOffset runAt, PdfValidationReport report, List<PdfExtractionDiagnostics> diagnostics,
        IReadOnlyList<PDFExtractionResult> fileResults, CancellationToken ct)
    {
        if (!_reportWriter.IsEnabled) return;

        await _reportWriter.WriteReportAsync(
            $"{ReportFolder}/{runAt:yyyy/MM/dd}/{runAt:HHmmssfff}-validation-report.json", report, ct);

        if (diagnostics.Count > 0)
            await _reportWriter.WriteReportAsync(
                $"{ReportFolder}/{runAt:yyyy/MM/dd}/{runAt:HHmmssfff}-diagnostics.json", diagnostics, ct);

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
        }).ToList();

        await _reportWriter.WriteReportAsync(
            $"{ReportFolder}/{runAt:yyyy/MM/dd}/{runAt:HHmmssfff}-file-facts.json", fileFacts, ct);
    }

    private sealed record PdfExtractionFailureReport(
        DateTimeOffset RunAt, string ExceptionType, string Message, string? StackTrace);

    // Dev-only (see IRunReportWriter.IsEnabled), same as WriteReportsAsync above - fallback
    // for a run that failed before a PdfValidationReport ever existed (blob listing,
    // cleaning, or PreviousRunCount threw), so there's still something written for that
    // run instead of silence.
    private async Task WriteFailureReportAsync(DateTimeOffset runAt, Exception failure, CancellationToken ct)
    {
        if (!_reportWriter.IsEnabled) return;

        await _reportWriter.WriteReportAsync(
            $"{ReportFolder}/{runAt:yyyy/MM/dd}/{runAt:HHmmssfff}-failure-report.json",
            new PdfExtractionFailureReport(runAt, failure.GetType().FullName ?? failure.GetType().Name, failure.Message, failure.StackTrace),
            ct);
    }

    // Maps the validated, cleaned records into the source-agnostic PdfExtractionOutput
    // returned to the caller. Several PdfExtractionOutput fields have no PDF equivalent and
    // are always null here (not "verified zero") — see PdfValidationReport's own comment:
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
        IReadOnlyList<PDFExtractionResult> fileResults,
        PdfValidationReport                report,
        PdfCleanResult                      cleanResult,
        int                                 errors,
        int                                 warnings,
        int                                 missingTitle,
        Dictionary<string, DateTimeOffset>  lastModifiedByBlob,
        Dictionary<string, ZenyaMetadata>   zenyaByBlob)
    {
        var nativeMetadataByBlob = BuildNativeMetadataLookup(fileResults);
        var sectionsByBlob       = BuildSectionsLookup(fileResults);
        var pageContextByKey     = BuildPageContextLookup(fileResults);

        var extractionDocs = cleanResult.Records
            .Select(r =>
            {
                var nativeMetadata = nativeMetadataByBlob.GetValueOrDefault(r.BlobName);
                var pageContext    = pageContextByKey.GetValueOrDefault((r.BlobName, r.PageNumber)) ?? PdfPageContext.Empty;
                var zenya          = zenyaByBlob.GetValueOrDefault(r.BlobName) ?? ZenyaMetadata.Empty;

                return new PdfExtractionDocument(
                    SourceId:              r.BlobName,
                    Ordinal:               r.PageNumber,
                    Content:               r.PageContent,
                    Title:                 r.Title,
                    Author:                nativeMetadata?.Author,
                    CreatedAt:             nativeMetadata?.CreatedAt,
                    ModDate:               nativeMetadata?.ModDate,
                    PageCount:             nativeMetadata?.PageCount,
                    LastModifiedDate:      lastModifiedByBlob.TryGetValue(r.BlobName, out var lm) ? lm : null,
                    ZenyaDocumentId:       zenya.DocumentId,
                    ZenyaVersion:          zenya.Version,
                    ZenyaStatus:           zenya.Status,
                    ZenyaUrl:              zenya.Url,
                    Bookmarks:             nativeMetadata?.Bookmarks ?? [],
                    Sections:              sectionsByBlob.GetValueOrDefault(r.BlobName) ?? [],
                    Breadcrumb:            pageContext.Breadcrumb,
                    Headings:              pageContext.Headings,
                    Boilerplate:           pageContext.Boilerplate,
                    Tables:                pageContext.Tables,
                    Dimensions:            pageContext.Dimensions,
                    SelectionMarks:        pageContext.SelectionMarks,
                    Figures:               pageContext.Figures,
                    Lines:                 pageContext.Lines);
            })
            .ToList();

        var issues = report.Issues
            .Take(MaxReturnedIssues)
            .Select(i => new ValidationIssueEntry(i.Stage, i.Severity, i.DocumentId, i.Message))
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

    // File-level native PDF facts (Author, CreatedAt, PageCount, Bookmarks) - same value
    // applies to every page of that file, so this is a per-blob lookup, not per-page like
    // PdfPageContext below.
    private static Dictionary<string, DocMetadata> BuildNativeMetadataLookup(
        IReadOnlyList<PDFExtractionResult> fileResults) =>
        fileResults
            .Where(f => f.Ok && f.NativeMetadata is not null)
            .ToDictionary(f => f.BlobName, f => f.NativeMetadata!, StringComparer.OrdinalIgnoreCase);

    // DI's semantic sections aren't page-scoped (a section's Elements can span pages), so
    // - like NativeMetadata above - this is file-level, duplicated across every page's
    // PdfExtractionDocument rather than filtered per page.
    private static Dictionary<string, IReadOnlyList<SectionInfo>> BuildSectionsLookup(
        IReadOnlyList<PDFExtractionResult> fileResults) =>
        fileResults
            .Where(f => f.Ok)
            .ToDictionary(f => f.BlobName, IReadOnlyList<SectionInfo> (f) => f.Structure?.Sections ?? [],
                StringComparer.OrdinalIgnoreCase);

    // Everything Structure/SectionBreadcrumbs knows about one page. Breadcrumb comes from
    // the bookmark outline (PDFSectionBreadCrumbBuilder, hierarchical - "Chapter 3 > 3.2
    // Dosage"); Headings from Document Intelligence's own title/sectionHeading-role
    // paragraph detection, which works even when the PDF has no outline at all. List fields
    // default to real empty (DI looked and found none), not "unknown" - only
    // Breadcrumb/Dimensions are genuinely nullable "no data" cases.
    private sealed record PdfPageContext(
        string?                          Breadcrumb,
        IReadOnlyList<Heading>           Headings,
        IReadOnlyList<Heading>           Boilerplate,
        IReadOnlyList<TableInfo>         Tables,
        PageDimensions?                  Dimensions,
        IReadOnlyList<SelectionMarkInfo> SelectionMarks,
        IReadOnlyList<FigureInfo>        Figures,
        IReadOnlyList<LineInfo>          Lines)
    {
        public static readonly PdfPageContext Empty = new(null, [], [], [], null, [], [], []);
    }

    // Sparse by design: only pages with at least one of these signals get an entry. A page
    // with none of them - nothing Structure has any data for at all - is a legitimate
    // "nothing to attach" case the caller handles via PdfPageContext.Empty, not a lookup
    // miss to work around.
    private static Dictionary<(string BlobName, int PageNumber), PdfPageContext> BuildPageContextLookup(
        IReadOnlyList<PDFExtractionResult> fileResults)
    {
        var lookup = new Dictionary<(string, int), PdfPageContext>();

        foreach (var file in fileResults.Where(f => f.Ok))
        {
            var headingsByPage = (file.Structure?.Headings ?? [])
                .GroupBy(h => h.PageNumber)
                .ToDictionary(g => g.Key, IReadOnlyList<Heading> (g) => g.ToList());

            var boilerplateByPage = (file.Structure?.Boilerplate ?? [])
                .GroupBy(h => h.PageNumber)
                .ToDictionary(g => g.Key, IReadOnlyList<Heading> (g) => g.ToList());

            var tablesByPage = (file.Structure?.Tables ?? [])
                .GroupBy(t => t.PageNumber)
                .ToDictionary(g => g.Key, IReadOnlyList<TableInfo> (g) => g.ToList());

            var dimensionsByPage = (file.Structure?.PageDimensions ?? [])
                .ToDictionary(d => d.PageNumber, d => d);

            var selectionMarksByPage = (file.Structure?.SelectionMarks ?? [])
                .GroupBy(s => s.PageNumber)
                .ToDictionary(g => g.Key, IReadOnlyList<SelectionMarkInfo> (g) => g.ToList());

            var figuresByPage = (file.Structure?.Figures ?? [])
                .GroupBy(f => f.PageNumber)
                .ToDictionary(g => g.Key, IReadOnlyList<FigureInfo> (g) => g.ToList());

            var linesByPage = (file.Structure?.Lines ?? [])
                .GroupBy(l => l.PageNumber)
                .ToDictionary(g => g.Key, IReadOnlyList<LineInfo> (g) => g.ToList());

            var pageNumbers = file.SectionBreadcrumbs.Keys
                .Concat(headingsByPage.Keys)
                .Concat(boilerplateByPage.Keys)
                .Concat(tablesByPage.Keys)
                .Concat(dimensionsByPage.Keys)
                .Concat(selectionMarksByPage.Keys)
                .Concat(figuresByPage.Keys)
                .Concat(linesByPage.Keys)
                .Distinct();

            foreach (var pageNumber in pageNumbers)
                lookup[(file.BlobName, pageNumber)] = new PdfPageContext(
                    Breadcrumb:     file.SectionBreadcrumbs.GetValueOrDefault(pageNumber),
                    Headings:       headingsByPage.GetValueOrDefault(pageNumber) ?? [],
                    Boilerplate:    boilerplateByPage.GetValueOrDefault(pageNumber) ?? [],
                    Tables:         tablesByPage.GetValueOrDefault(pageNumber) ?? [],
                    Dimensions:     dimensionsByPage.GetValueOrDefault(pageNumber),
                    SelectionMarks: selectionMarksByPage.GetValueOrDefault(pageNumber) ?? [],
                    Figures:        figuresByPage.GetValueOrDefault(pageNumber) ?? [],
                    Lines:          linesByPage.GetValueOrDefault(pageNumber) ?? []);
        }

        return lookup;
    }

    // Pure counts derived from the report/cleanResult — no side effects, safe to compute
    // independently of whether EmitValidationTelemetry below ever runs. BuildExtractionOutput
    // needs these on the success path; EmitValidationTelemetry recomputes them itself so
    // its own (always-run, finally-block) emission doesn't depend on the success path
    // having reached this call.
    private static (int Errors, int Warnings, int MissingTitle) CountIssues(
        PdfValidationReport report, PdfCleanResult cleanResult)
    {
        var errors   = report.Issues.Count(i => i.Severity == "Error");
        var warnings = report.Issues.Count(i => i.Severity != "Error");

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
    private void EmitValidationTelemetry(PdfValidationReport report, PdfCleanResult cleanResult)
    {
        foreach (var warning in report.MagnitudeWarnings)
            _logger.LogWarning("{Warning}", warning);

        _logger.LogInformation("PDF validation {Result} — {Cleaned} records, {Issues} issues",
            report.Passed ? "passed" : "failed", report.CleanedRecords, report.Issues.Count);

        foreach (var issue in report.Issues.Take(MaxLoggedIssues))
            _logger.Log(
                issue.Severity == "Error" ? LogLevel.Error : LogLevel.Warning,
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
    }

    // Stable hash of the PDF's raw bytes, used as a dedup/caching key:
    // - Same file content -> same hash, regardless of the blob's file name.
    // - Would let a future caller detect "this exact file was already processed" and skip
    //   paying for another extraction call - not wired into a skip decision yet, since
    //   there's nowhere that stores previously-seen hashes across runs.
    private static string ComputeContentHash(byte[] pdfBytes) =>
        Convert.ToHexString(SHA256.HashData(pdfBytes));

    private async Task<(int? Count, ETag? ETag)> PreviousRunCount(CancellationToken ct)
    {
        var (state, etag) = await _blobStore.TryReadJsonWithETagAsync<RunState>(_stateContainer, StateBlobName, ct);
        return (state?.CleanedRecords, etag);
    }

    private Task SaveRunStateAsync(int cleanedRecords, ETag? previousETag, CancellationToken ct) =>
        _blobStore.SaveJsonWithETagAsync(_stateContainer, StateBlobName, new RunState(cleanedRecords), previousETag, ct);
}
