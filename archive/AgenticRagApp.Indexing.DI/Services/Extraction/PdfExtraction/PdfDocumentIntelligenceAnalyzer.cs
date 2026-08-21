using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Azure;
using Azure.AI.DocumentIntelligence;
using Microsoft.Extensions.Logging;
using AgenticRagApp.Infrastructure.Clients.DocumentIntelligence;
using AgenticRagApp.Indexing.DI.Models;
using AgenticRagApp.Common.Models;
using AgenticRagApp.Observability;

namespace AgenticRagApp.Indexing.DI.Services;

// Everything Document Intelligence (DI) needs for one PDF, except preflight checks
// and PdfPig-native reads.
// - Makes the one paid analyze call, polls for completion itself, and retries only
//   the free status poll on 429/5xx/transient network errors; never resubmits the
//   paid POST.
// - Validates the response (markdown format, non-empty) before trusting any offset.
// - Extracts every DI structural feature into PdfDocumentStructure.
// - Surfaces DI's own warnings plus whatever this class flags along the way.
// partial: the regexes below are compile-time [GeneratedRegex] source-generated, which
// costs nothing at startup (unlike RegexOptions.Compiled).
public sealed partial class PdfDocumentIntelligenceAnalyzer
{
    // --- Tuning ---------------------------------------------------------------

    // https://azure.microsoft.com/en-us/pricing/details/document-intelligence/
    // "All Prebuilt Models: Document, Layout, Receipt, Invoice, ID, W-2, 1098 Tax
    // forms, Health insurance card, Contract" - $10 per 1,000 pages, which covers
    // "prebuilt-layout" (what this class submits). $10 / 1,000 pages = $0.01/page at
    // time of writing. Verify current pricing at that link before trusting any
    // estimate built on this constant - Document Intelligence's own API response
    // never includes actual cost/usage, so this is a local approximation, not a
    // number read back from the service.
    // internal (not private): shared with GetQualityWarningsHelper (DocumentIntelligenceHelpers/).
    internal const decimal CostPerPage = 0.01m;

    // GetLines is the bulkiest thing this class produces - one LineInfo per OCR line,
    // across every page. Was defaulted off (dev reports only) until A3 (pre-chunking-
    // action-items.md) gave it a real consumer: GetFontSizeWarningsHelper needs every
    // line's polygon to derive a per-document body-text baseline. Source grounding
    // (the future highlight-on-source join GetLines also exists for) will be a second
    // consumer later, not the reason this is on now.
    private const bool IncludeLines = true;

    // BackoffDelays/PollingInterval/AnalyzeBudget* now live in DocumentAnalysisPoller
    // (DocumentIntelligenceHelpers/), the only caller.

    private readonly IDocumentAnalysisClient _diClient;
    private readonly ILogger _logger;

    // Backoff/poll-interval waits go through this instead of a bare Task.Delay so tests
    // can substitute an instant no-op - retry backoff (submit and poll) is on real
    // TimeSpan schedules (seconds), and a test exercising the retry-exhaustion path
    // would otherwise actually wait through it in wall-clock time.
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public PdfDocumentIntelligenceAnalyzer(
        IDocumentAnalysisClient diClient, ILogger<PdfDocumentIntelligenceAnalyzer> logger,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _diClient = diClient;
        _logger   = logger;
        _delay    = delay ?? Task.Delay;
    }

    // --- Entry point ----------------------------------------------------------

    // Called after preflight/native-metadata have already run. Expects the caller to have:
    // - Validated the PDF (PdfDocumentValidator.IsPDFValid).
    // - Read nativeMetadata (PdfNativeMetadataExtractor) and closed the PdfDocument;
    //   this method only ever sees the resulting data, never the PdfDocument.
    // ct is required (no `= default`): every path here can block on network I/O, so a
    // caller that can't cancel is a caller that can hang.
    public async Task<DocumentAnalyzedResults> AnalyzeDocumentAsync(
        byte[] pdfBytes, string blobName, DocMetadata nativeMetadata, CancellationToken ct)
    {
        var outcome = await AnalyzeWithMetricsAsync(pdfBytes, blobName, nativeMetadata.PageCount, ct);

        if (!TryValidateAnalyzeOutcome(outcome, blobName, out var analysis, out var error))
        {
            // The individual failure (exception, throttling, bad content format, ...)
            // was already logged with full detail at the point it happened, deeper in
            // AnalyzeWithMetricsAsync/SubmitAndPollAsync/ValidateAnalyzeResult. This is
            // the one-line summary at the boundary: what this document's overall
            // outcome was, tagged with Reason.Code so failures are greppable/aggregable
            // without parsing Message.
            _logger.LogWarning(
                "Document Intelligence analysis of '{Blob}' failed ({Reason}): {Message}",
                blobName, error.Reason?.Code, error.Message);

            // Warnings gathered before the failure still ship: diagnostics matter most
            // on the path that failed.
            return new DocumentAnalyzedResults(false, null, null, null, null, error)
            {
                Warnings = outcome.Warnings,
                Infos    = [],
            };
        }

        try
        {
            return BuildResults(analysis, blobName, nativeMetadata, outcome);
        }
        catch (Exception ex)
        {
            // The analysis itself succeeded and is already paid for; this is a defect in
            // our own extraction over a response shape we didn't anticipate. Typed like
            // every other failure so it can't escape past the caller's error handling.
            _logger.LogError(ex,
                "Failed to extract structure from a successful Document Intelligence response for '{Blob}'.",
                blobName);

            return new DocumentAnalyzedResults(false, null, null, null, null,
                PipelineIssue.Error(
                    PipelineStage.ParsePages,
                    blobName,
                    $"Extraction from the Document Intelligence response failed: {ex.Message}",
                    reason: PdfOpenFailureReason.Unknown))
            {
                Warnings = outcome.Warnings,
                Infos    = [],
            };
        }
    }

    // Everything from a validated AnalyzeResult to the finished DocumentAnalyzedResults.
    // Split out of AnalyzeDocumentAsync only so the try/catch above stays readable.
    // internal (not private): tests build a real AnalyzeResult via
    // ModelReaderWriter.Read<AnalyzeResult>(json) and call this directly, same as GetPages.
    internal DocumentAnalyzedResults BuildResults(
        AnalyzeResult analysis, string blobName, DocMetadata nativeMetadata, AnalyzeOutcome outcome)
    {
        var title = GetTitleHelper.GetTitle(nativeMetadata, blobName);

        var (pages, pageWarnings, pageInfos) = GetPagesHelper.GetPages(_logger, analysis, blobName, title);

        // DI is expected to return exactly one PdfPageRecord per native PDF page. A
        // mismatch means DI silently truncated (or over-produced) pages - every
        // downstream check (Extract->Clean reconciliation, the diff step) only ever
        // sees the already-truncated result and would pass it as a clean, complete
        // document. This is the one place both counts exist, so it's the only place
        // that can catch it. Folded into Ok=false rather than a warning: a truncated
        // document must not reach the index looking complete.
        if (pages.Count != nativeMetadata.PageCount)
        {
            _logger.LogWarning(
                "'{Blob}': Document Intelligence returned {DiPages} page(s), native PDF has {NativePages}.",
                blobName, pages.Count, nativeMetadata.PageCount);

            return new DocumentAnalyzedResults(false, analysis.Content, null, null, null,
                PipelineIssue.Error(
                    PipelineStage.ParsePages,
                    blobName,
                    $"Document Intelligence returned {pages.Count} page(s), expected {nativeMetadata.PageCount} (native PDF page count).",
                    reason: PdfOpenFailureReason.TruncatedPages))
            {
                Warnings = [.. outcome.Warnings, .. GetQualityWarningsHelper.GetDiWarnings(analysis), .. pageWarnings,
                            .. GetQualityWarningsHelper.GetZeroWordWarnings(analysis, blobName)],
                Infos    = pageInfos,
            };
        }

        var zeroWordWarnings = GetQualityWarningsHelper.GetZeroWordWarnings(analysis, blobName);
        var tables  = GetTablesHelper.GetTables(analysis);
        var figures = GetFiguresHelper.GetFigures(analysis);
        var headingsResult = GetHeadingsHelper.GetHeadings(analysis);
        var pageDimensions = GetPageDimensionsHelper.GetPageDimensions(analysis);

        pages = GetPictureOnlyPagesHelper.MarkPictureOnlyPages(analysis, pages, figures);

        var (pageDimensionWarnings, pageDimensionInfos) = PageDimensionWarningsHelper.GetPageDimensionWarnings(
            nativeMetadata.NativePageDimensions, pageDimensions, blobName);

        var estimatedCost = pages.Count * CostPerPage;
        var lines         = IncludeLines ? GetLinesHelper.GetLines(analysis) : [];

        return new DocumentAnalyzedResults(
            true,
            analysis.Content,
            pages,
            new PdfDocumentStructure(
                Headings:       headingsResult.Headings,
                Boilerplate:    GetBoilerplateHelper.GetBoilerplate(analysis),
                Tables:         tables,
                PageDimensions: pageDimensions,
                SelectionMarks: GetSelectionMarksHelper.GetSelectionMarks(analysis),
                Figures:        figures,
                Lines:          lines,
                Sections:       GetSectionsHelper.GetSections(analysis)),
            estimatedCost,
            null)
        {
            // Merges warnings/infos from every stage into one list each, regardless of
            // which stage found them - a spread collection expression rather than
            // List<T>(capacity)+AddRange, since each list here is consumed exactly once.
            Warnings = [.. outcome.Warnings, .. GetQualityWarningsHelper.GetDiWarnings(analysis), .. pageWarnings,
                        .. zeroWordWarnings, .. GetQualityWarningsHelper.StructureWarnings(tables, figures, blobName),
                        .. GetQualityWarningsHelper.HeadingWarnings(headingsResult.Headings, headingsResult.NumberedLabelsSeen, headingsResult.PairedHeadingMerges, blobName),
                        .. GetFontSizeWarningsHelper.GetFontSizeWarnings(headingsResult.Headings, lines, blobName),
                        .. pageDimensionWarnings],
            Infos    = [.. pageInfos, GetQualityWarningsHelper.CostInfo(estimatedCost, pages.Count, blobName), .. pageDimensionInfos],
            Language = LanguageDetectionHelper.Detect(analysis),
        };
    }

    // --- Document Intelligence call -------------------------------------------

    // Wraps the submit/poll cycle in the operational-health metrics (throttling,
    // wall-clock), which are recorded regardless of outcome: they describe the call,
    // not the file's content quality.
    private async Task<AnalyzeOutcome> AnalyzeWithMetricsAsync(
        byte[] pdfBytes, string blobName, int pageCount, CancellationToken ct)
    {
        _logger.LogInformation("Submitting '{Blob}' to Document Intelligence.", blobName);

        // Markdown (vs DI's default plain text): renders tables as HTML <table> with
        // real rowspan/colspan (GetTables depends on it) and keeps Content in the shape
        // downstream chunking consumes. Every Offset in this file assumes this format.
        var options = new AnalyzeDocumentOptions("prebuilt-layout", BinaryData.FromBytes(pdfBytes))
        {
            OutputContentFormat = DocumentContentFormat.Markdown,
        };

        var sw = Stopwatch.StartNew();
        var throttleRetries = 0;

        try
        {
            var poll = await DocumentAnalysisPoller.SubmitAndPollAsync(
                _diClient, _logger, _delay, blobName, options, pageCount, ct, ValidateAnalyzeResult);
            throttleRetries = poll.ThrottleRetries;
            return poll.Outcome;
        }
        finally
        {
            Instrumentation.DiAnalyzeDuration.Record(
                sw.Elapsed.TotalSeconds,
                new KeyValuePair<string, object?>("page_bucket", PageBucket(pageCount)),
                new KeyValuePair<string, object?>("outcome", throttleRetries > 0 ? "throttled" : "clean"));
            if (throttleRetries > 0)
                Instrumentation.DiThrottleRetries.Add(throttleRetries);
        }
    }

    // Buckets rather than raw page count: keeps the duration histogram's tag
    // cardinality bounded while still letting per-page cost be read off per bucket,
    // which a single unbucketed distribution mixing 3-page and 800-page documents
    // can't give you - see AnalyzeBudgetPerPage's derivation.
    private static string PageBucket(int pageCount) => pageCount switch
    {
        <= 0    => "unknown",
        <= 10   => "1-10",
        <= 50   => "11-50",
        <= 200  => "51-200",
        <= 1000 => "201-1000",
        _       => "1000+",
    };

    // PollResult/SubmitAndPollAsync/BackoffDelays/PollingInterval/AnalyzeBudget*/
    // IsRetryablePollFailure/RetryAfter/MinRetryAfter/Fail/RequestFailure/Unexpected
    // now live in DocumentAnalysisPoller (DocumentIntelligenceHelpers/).

    // Folds two failure signals into the one question callers have ("do I have a usable
    // analysis?"), using the same Try(out, out) + [NotNullWhen] shape as
    // PdfDocumentValidator.IsPDFValid so the compiler, not a comment, proves the nullability.
    // Both LogError branches describe a bug in *this* class, not a DI-side failure;
    // hence LogError rather than the LogWarning used everywhere else.
    private bool TryValidateAnalyzeOutcome(
        AnalyzeOutcome outcome, string blobName,
        [NotNullWhen(true)]  out AnalyzeResult?   result,
        [NotNullWhen(false)] out PipelineIssue? error)
    {
        if (!outcome.Ok)
        {
            result = null;

            if (outcome.Error is not null)
            {
                error = outcome.Error;
                return false;
            }

            _logger.LogError(
                "'{Blob}': analysis reported Ok=false but Error was null; bug in SubmitAndPollAsync/ValidateAnalyzeResult.",
                blobName);

            error = PipelineIssue.Error(
                PipelineStage.ParsePages,
                blobName,
                "Document Intelligence analysis failed with no error details.",
                reason: PdfOpenFailureReason.Unknown);
            return false;
        }

        if (outcome.Result is not null)
        {
            result = outcome.Result;
            error  = null;
            return true;
        }

        _logger.LogError(
            "'{Blob}': analysis reported Ok=true but returned no result; bug in SubmitAndPollAsync/ValidateAnalyzeResult.",
            blobName);

        result = null;
        error  = PipelineIssue.Error(
            PipelineStage.ParsePages,
            blobName,
            "Document Intelligence analysis reported success but returned no result.",
            reason: PdfOpenFailureReason.MissingAnalysisResult);
        return false;
    }

    // --- Response validation --------------------------------------------------

    // Cheapest and most fundamental first, so a bad response fails fast:
    // 1. Content format must be Markdown (an O(1) enum compare, and the trust boundary
    //    every Offset below depends on).
    // 2. At least one page (an O(1) count). Preflight already rejects zero-page PDFs,
    //    so zero pages here means the analysis failed, not that the document was empty.
    // 3. Non-BMP scan last: it is the only O(content length) check, and running it
    //    before step 2 meant a full pass whose warnings were then discarded.
    // internal (not private): testable directly against a hand-built AnalyzeResult, as
    // with GetPages/BuildResults above.
    internal AnalyzeOutcome ValidateAnalyzeResult(AnalyzeResult result, string blobName)
    {
        if (result.ContentFormat != DocumentContentFormat.Markdown)
        {
            _logger.LogWarning(
                "Document Intelligence returned unexpected content format '{Format}' for '{Blob}'.",
                result.ContentFormat, blobName);

            return DocumentAnalysisPoller.Fail(blobName,
                $"Document Intelligence returned content format '{result.ContentFormat}', expected Markdown.",
                PdfOpenFailureReason.UnexpectedContentFormat);
        }

        if (result.Pages is not { Count: > 0 })
        {
            _logger.LogWarning("Document Intelligence returned zero pages for '{Blob}'.", blobName);

            return DocumentAnalysisPoller.Fail(blobName,
                "Document Intelligence analysis returned zero pages.",
                PdfOpenFailureReason.EmptyDocument);
        }

        return new AnalyzeOutcome(true, result, null)
        {
            Warnings = CheckNonBmpCharacters(result, blobName),
        };
    }

    

    // Diagnostic only, never fails. Flags characters needing a UTF-16 surrogate pair
    // (emoji, some math symbols; NOT ordinary Dutch diacritics, which fit in one unit).
    // - Why it matters: every Offset here assumes UTF-16 code-unit offsets, which is
    //   exactly what Substring expects. A surrogate pair doesn't prove anything is
    //   broken; it's a signal worth checking if garbled content shows up downstream.
    // - Single allocation-free pass over the span; counts well-formed pairs exactly
    //   (the old rune-difference trick boxed the enumerator and mis-counted lone surrogates).
    private IReadOnlyList<AnalysisWarning> CheckNonBmpCharacters(AnalyzeResult result, string blobName)
    {
        var nonBmpCount = CountSurrogatePairs(result.Content);
        if (nonBmpCount == 0) return [];

        _logger.LogWarning(
            "'{Blob}' contains {Count} non-BMP character(s) (UTF-16 surrogate pairs) in its analyzed content.",
            blobName, nonBmpCount);

        return [new AnalysisWarning(
            "NonBmpCharacters",
            $"Content contains {nonBmpCount} non-BMP character(s) (UTF-16 surrogate pairs).",
            blobName)];
    }

    // internal (not private): unit tested directly with hand-built strings/spans.
    internal static int CountSurrogatePairs(ReadOnlySpan<char> content)
    {
        var count = 0;
        for (var i = 0; i + 1 < content.Length; i++)
            if (char.IsHighSurrogate(content[i]) && char.IsLowSurrogate(content[i + 1]))
            {
                count++;
                i++;
            }
        return count;
    }

    // --- Pages and content ----------------------------------------------------

    // --- Structure ------------------------------------------------------------
    // ToHeading/FirstOffset/FirstPage/ToPolygonPoints now live in DiGeometryHelpers
    // (DocumentIntelligenceHelpers/) - shared by GetHeadingsHelper/GetBoilerplateHelper/
    // GetTablesHelper/GetFiguresHelper/GetLinesHelper/GetSelectionMarksHelper.

    // --- Quality --------------------------------------------------------------
    // GetZeroWordWarnings/StructureWarnings/CostInfo/GetDiWarnings now live in
    // GetQualityWarningsHelper (DocumentIntelligenceHelpers/).
}
