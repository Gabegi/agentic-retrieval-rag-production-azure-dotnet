using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Azure;
using Azure.AI.DocumentIntelligence;
using Microsoft.Extensions.Logging;
using AgenticRagApp.Infrastructure.Clients.DocumentIntelligence;
using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Common.Models;
using AgenticRagApp.Observability;

namespace AgenticRagApp.Indexing.Pdf.Services
{
    // Everything Document Intelligence (DI) needs for one PDF, except preflight checks
    // and PdfPig-native reads.
    // - Makes the one paid analyze call, polls for completion itself, and retries only
    //   the free status poll on 429; never resubmits the paid POST.
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
        private const decimal CostPerPage = 0.01m;

        // Backoff after *consecutive* 429s on the status poll, per Microsoft's documented
        // 2-5-13-34 pattern. A Retry-After header, when present, wins over this schedule.
        private static readonly TimeSpan[] BackoffDelays =
        [
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(13), TimeSpan.FromSeconds(34)
        ];

        // Ordinary interval between status-poll GETs; unrelated to BackoffDelays, which
        // only applies after a 429. Microsoft advises no more than one poll per 2s.
        private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(2);

        // Hard ceiling on one document's analysis, so a server-side operation that never
        // reports completion can't pin a worker forever. Only applies when the caller's
        // own token has no earlier deadline; see AnalyzeDocumentAsync.
        private static readonly TimeSpan MaxAnalyzeDuration = TimeSpan.FromMinutes(10);

        private readonly IDocumentAnalysisClient _diClient;
        private readonly ILogger _logger;

        public PdfDocumentIntelligenceAnalyzer(
            IDocumentAnalysisClient diClient, ILogger<PdfDocumentIntelligenceAnalyzer> logger)
        {
            _diClient = diClient;
            _logger   = logger;
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
            var outcome = await AnalyzeWithMetricsAsync(pdfBytes, blobName, ct);

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

            var title = GetTitle(nativeMetadata, blobName);

            var (pages, pageWarnings, pageInfos) = GetPages(analysis, blobName, title);
            var zeroWordWarnings = GetZeroWordWarnings(analysis, blobName);
            var tables  = GetTables(analysis);
            var figures = GetFigures(analysis);

            var estimatedCost = pages.Count * CostPerPage;

            return new DocumentAnalyzedResults(
                true,
                analysis.Content,
                pages,
                new PdfDocumentStructure(
                    Headings:       GetHeadings(analysis),
                    Boilerplate:    GetBoilerplate(analysis),
                    Tables:         tables,
                    PageDimensions: GetPageDimensions(analysis),
                    SelectionMarks: GetSelectionMarks(analysis),
                    Figures:        figures,
                    Lines:          GetLines(analysis),
                    Sections:       GetSections(analysis)),
                estimatedCost,
                null)
            {
                // Merges warnings/infos from every stage into one list each, regardless of
                // which stage found them - a spread collection expression rather than
                // List<T>(capacity)+AddRange, since each list here is consumed exactly once.
                Warnings = [.. outcome.Warnings, .. GetDiWarnings(analysis), .. pageWarnings,
                            .. zeroWordWarnings, .. StructureWarnings(tables, figures, blobName)],
                Infos    = [.. pageInfos, CostInfo(estimatedCost, pages.Count, blobName)],
            };
        }

        // --- Document Intelligence call -------------------------------------------

        // Wraps the submit/poll cycle in the operational-health metrics (throttling,
        // wall-clock), which are recorded regardless of outcome: they describe the call,
        // not the file's content quality.
        private async Task<AnalyzeOutcome> AnalyzeWithMetricsAsync(
            byte[] pdfBytes, string blobName, CancellationToken ct)
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
                var poll = await SubmitAndPollAsync(blobName, options, ct);
                throttleRetries = poll.ThrottleRetries;
                return poll.Outcome;
            }
            finally
            {
                Instrumentation.DiAnalyzeDuration.Record(sw.Elapsed.TotalSeconds);
                if (throttleRetries > 0)
                    Instrumentation.DiThrottleRetries.Add(throttleRetries);
            }
        }

        private readonly record struct PollResult(AnalyzeOutcome Outcome, int ThrottleRetries);

        // Submits once (WaitUntil.Started), then polls manually instead of using
        // WaitUntil.Completed.
        // - Why: SDK bug Azure/azure-sdk-for-net#50904 means DI's internal polling can still
        //   hit 429 even with client-level retry. Retrying that resubmits from scratch and
        //   pays for a whole new analysis (up to 5x cost under sustained throttling).
        //   Polling here means a 429 only ever retries the free GET.
        // - Backoff is indexed by *consecutive* 429s, not by poll count. Indexing by poll
        //   count silently spent the whole retry budget on successful polls, so any document
        //   taking more than BackoffDelays.Length * PollingInterval to analyze had zero
        //   retries left by the time a 429 could occur.
        // - Retry-After, when the service sends it, is authoritative over BackoffDelays.
        // - OperationCanceledException from the caller's own token propagates (host
        //   shutdown, not a per-document failure). The internal timeout becomes a typed error.
        private async Task<PollResult> SubmitAndPollAsync(
            string blobName, AnalyzeDocumentOptions options, CancellationToken ct)
        {
            // Linked so the caller's token still wins if it fires first; CancelAfter only
            // adds a ceiling, it never extends the caller's deadline.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(MaxAnalyzeDuration);
            var pollCt = timeoutCts.Token;

            var throttleRetries = 0;

            try
            {
                Operation<AnalyzeResult> operation;
                try
                {
                    operation = await _diClient.SubmitAnalyzeAsync(options, pollCt);
                }
                catch (RequestFailedException ex)
                {
                    _logger.LogWarning(ex, "Document Intelligence rejected the analyze submission for '{Blob}'.", blobName);
                    return new PollResult(RequestFailure(blobName, ex), throttleRetries);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Unexpected error submitting '{Blob}' to Document Intelligence.", blobName);
                    return new PollResult(Unexpected(blobName, ex), throttleRetries);
                }

                // Consecutive-429 counter: reset by any successful poll, so a document that
                // is throttled, recovers, then is throttled again gets a fresh budget.
                var consecutive429 = 0;

                while (!operation.HasCompleted)
                {
                    try
                    {
                        await operation.UpdateStatusAsync(pollCt);
                        consecutive429 = 0;
                    }
                    catch (RequestFailedException ex) when (ex.Status == 429 && consecutive429 < BackoffDelays.Length)
                    {
                        var wait = RetryAfter(ex) ?? BackoffDelays[consecutive429];
                        consecutive429++;
                        throttleRetries++;

                        _logger.LogWarning(
                            "DI throttled polling '{Blob}' (consecutive 429 #{Count}); backing off {Wait}.",
                            blobName, consecutive429, wait);

                        await Task.Delay(wait, pollCt);
                        continue;
                    }
                    catch (RequestFailedException ex)
                    {
                        if (ex.Status == 429)
                            _logger.LogWarning(ex,
                                "Document Intelligence throttled '{Blob}'; retries exhausted after {Count} consecutive 429(s).",
                                blobName, consecutive429);
                        else
                            _logger.LogWarning(ex, "Document Intelligence failed while polling '{Blob}'.", blobName);

                        return new PollResult(RequestFailure(blobName, ex), throttleRetries);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Unexpected error polling '{Blob}' with Document Intelligence.", blobName);
                        return new PollResult(Unexpected(blobName, ex), throttleRetries);
                    }

                    if (!operation.HasCompleted)
                        await Task.Delay(PollingInterval, pollCt);
                }

                // Guard before .Value: an LRO that completes *without* a value throws
                // InvalidOperationException from Value (Azure/azure-sdk-for-net#27516), which
                // no handler here would catch and no typed error would describe. A non-success
                // completion normally surfaces as RequestFailedException from UpdateStatusAsync
                // and is handled above; this covers the remaining case.
                if (!operation.HasValue)
                {
                    _logger.LogWarning("Document Intelligence completed '{Blob}' with no result value.", blobName);
                    return new PollResult(
                        Fail(blobName,
                            "Document Intelligence operation completed without a result.",
                            PdfOpenFailureReason.MissingAnalysisResult),
                        throttleRetries);
                }

                return new PollResult(ValidateAnalyzeResult(operation.Value, blobName), throttleRetries);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Our ceiling fired, not the caller's token: a per-document failure, not a shutdown.
                _logger.LogWarning(
                    "Document Intelligence analysis of '{Blob}' exceeded {Limit}; abandoning.", blobName, MaxAnalyzeDuration);
                return new PollResult(
                    Fail(blobName,
                        $"Document Intelligence analysis timed out after {MaxAnalyzeDuration.TotalMinutes:F0} minute(s).",
                        PdfOpenFailureReason.DiServiceError),
                    throttleRetries);
            }
        }

        // Folds two failure signals into the one question callers have ("do I have a usable
        // analysis?"), using the same Try(out, out) + [NotNullWhen] shape as
        // PdfDocumentValidator.IsPDFValid so the compiler, not a comment, proves the nullability.
        // Both LogError branches describe a bug in *this* class, not a DI-side failure;
        // hence LogError rather than the LogWarning used everywhere else.
        private bool TryValidateAnalyzeOutcome(
            AnalyzeOutcome outcome, string blobName,
            [NotNullWhen(true)]  out AnalyzeResult?   result,
            [NotNullWhen(false)] out ExtractionError? error)
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

                error = new ExtractionError(
                    RowNumber:  0,
                    DocumentId: blobName,
                    Message:    "Document Intelligence analysis failed with no error details.",
                    Reason:     PdfOpenFailureReason.Unknown);
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
            error  = new ExtractionError(
                RowNumber:  0,
                DocumentId: blobName,
                Message:    "Document Intelligence analysis reported success but returned no result.",
                Reason:     PdfOpenFailureReason.MissingAnalysisResult);
            return false;
        }

        // Retry-After is the service's own instruction and beats any fixed schedule.
        // Sent either as delta-seconds or as an HTTP-date; both are accepted here.
        private static TimeSpan? RetryAfter(RequestFailedException ex)
        {
            if (ex.GetRawResponse() is not { } response ||
                !response.Headers.TryGetValue("Retry-After", out var raw) ||
                string.IsNullOrWhiteSpace(raw))
                return null;

            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
                return seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;

            if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var when))
            {
                var delay = when - DateTimeOffset.UtcNow;
                return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
            }

            return null;
        }

        // --- Typed failures -------------------------------------------------------

        // RowNumber is 0 here, deliberately, and is NOT the same convention as
        // ExtractionWarning's nullable RowNumber (which PdfNativeMetadataExtractor leaves
        // null for PDFs). ExtractionError.RowNumber is non-nullable and shared with the CSV
        // extractor; making it int? is a cross-cutting change to every producer, not
        // something to fold in here.
        private static AnalyzeOutcome Fail(string blobName, string message, PdfOpenFailureReason reason) =>
            new(false, null, new ExtractionError(
                RowNumber:  0,
                DocumentId: blobName,
                Message:    message,
                Reason:     reason));

        private static AnalyzeOutcome RequestFailure(string blobName, RequestFailedException ex) =>
            Fail(blobName,
                $"Document Intelligence request failed ({ex.Status}): {ex.Message}",
                ex.Status == 429 ? PdfOpenFailureReason.Throttled : PdfOpenFailureReason.DiServiceError);

        private static AnalyzeOutcome Unexpected(string blobName, Exception ex) =>
            Fail(blobName,
                $"Document Intelligence analysis failed unexpectedly: {ex.Message}",
                PdfOpenFailureReason.Unknown);

        // --- Response validation --------------------------------------------------

        // Cheapest and most fundamental first, so a bad response fails fast:
        // 1. Content format must be Markdown (an O(1) enum compare, and the trust boundary
        //    every Offset below depends on).
        // 2. At least one page (an O(1) count). Preflight already rejects zero-page PDFs,
        //    so zero pages here means the analysis failed, not that the document was empty.
        // 3. Non-BMP scan last: it is the only O(content length) check, and running it
        //    before step 2 meant a full pass whose warnings were then discarded.
        private AnalyzeOutcome ValidateAnalyzeResult(AnalyzeResult result, string blobName)
        {
            if (result.ContentFormat != DocumentContentFormat.Markdown)
            {
                _logger.LogWarning(
                    "Document Intelligence returned unexpected content format '{Format}' for '{Blob}'.",
                    result.ContentFormat, blobName);

                return Fail(blobName,
                    $"Document Intelligence returned content format '{result.ContentFormat}', expected Markdown.",
                    PdfOpenFailureReason.UnexpectedContentFormat);
            }

            if (result.Pages is not { Count: > 0 })
            {
                _logger.LogWarning("Document Intelligence returned zero pages for '{Blob}'.", blobName);

                return Fail(blobName,
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

        private static int CountSurrogatePairs(ReadOnlySpan<char> content)
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

        // nativeMetadata.Title when the PDF actually sets one, else derived from the blob name.
        // - GetFileNameWithoutExtension, not Split('/')[0]: for "protocols/policy-2024.pdf"
        //   the latter returns the folder, not the file.
        private static string GetTitle(DocMetadata nativeMetadata, string blobName) =>
            !string.IsNullOrWhiteSpace(nativeMetadata.Title)
                ? nativeMetadata.Title
                : Path.GetFileNameWithoutExtension(blobName.AsSpan()).ToString().Replace('-', ' ');

        // One PdfPageRecord per page, sliced from Content by each page's own Spans (DI's
        // structural page model), not by splitting on "<!-- PageBreak -->".
        // Per page:
        // - Slice by Spans, strip DI's noise comments (PageHeader/Footer/Number/FigureContent),
        //   normalize a setext title ("Title" + "===") to ATX ("# Title").
        // - Warn if that leaves the page empty; an empty page shouldn't reach the index unnoticed.
        // - Warn (never repair) on unbalanced <table> tags: a table split across pages is
        //   handled later by the chunk-builder's Sections-based boundaries, so this is a
        //   frequency signal, not a fix.
        // Both cleanups touch PageContent only, never Content: they change string length,
        // which would shift every offset into the offset-addressable source.
        // internal (not private): tests build a real AnalyzeResult via
        // ModelReaderWriter.Read<AnalyzeResult>(json) and call this without a live DI call.
        internal (IReadOnlyList<PdfPageRecord> Pages,
                  IReadOnlyList<AnalysisWarning> Warnings,
                  IReadOnlyList<AnalysisWarning> Infos) GetPages(
            AnalyzeResult result, string blobName, string title)
        {
            var pages    = new List<PdfPageRecord>(result.Pages.Count);
            var warnings = new List<AnalysisWarning>();

            var setextNormalized   = 0;
            var noiseStripped      = 0;
            var pagesWithNoise     = 0;
            var truncatedSpans     = 0;

            foreach (var p in result.Pages)
            {
                var content = SliceBySpans(result.Content, p.Spans, ref truncatedSpans);

                // One regex pass each, counting and replacing together: same cost as the
                // plain Replace it stands in for, not two scans per pattern.
                var pageNoise = 0;
                content = NoiseCommentLineRegex().Replace(content, _ => { pageNoise++; return ""; });
                if (pageNoise > 0)
                {
                    noiseStripped += pageNoise;
                    pagesWithNoise++;
                }

                // TrimEnd: the title group excludes \r and \n but not trailing spaces/tabs,
                // which markdown would otherwise carry into the ATX heading.
                content = SetextTitleRegex().Replace(content, m =>
                {
                    setextNormalized++;
                    return "# " + m.Groups["title"].Value.TrimEnd();
                });

                content = content.Trim('\r', '\n');

                if (content.Length == 0)
                {
                    _logger.LogWarning(
                        "'{Blob}' page {Page} has no content (no Spans); an empty page could reach the index unnoticed.",
                        blobName, p.PageNumber);

                    warnings.Add(new AnalysisWarning(
                        "EmptyPageContent",
                        $"Page {p.PageNumber} has no content (no Spans); an empty page could reach the index unnoticed.",
                        blobName));
                }

                var open  = TableOpenTagRegex().Count(content);
                var close = TableCloseTagRegex().Count(content);
                if (open != close)
                {
                    _logger.LogWarning(
                        "'{Blob}' page {Page} has unbalanced <table> tags ({Open} open, {Close} close); likely split across a page boundary.",
                        blobName, p.PageNumber, open, close);

                    warnings.Add(new AnalysisWarning(
                        "UnbalancedTableTags",
                        $"Page {p.PageNumber} has {open} <table> open tag(s) but {close} close tag(s); likely split across a page boundary.",
                        blobName));
                }

                pages.Add(new PdfPageRecord
                {
                    BlobName    = blobName,
                    PageNumber  = p.PageNumber,
                    PageContent = content,
                    Title       = title,
                });
            }

            if (truncatedSpans > 0)
                warnings.Add(new AnalysisWarning(
                    "SpanOutOfRange",
                    $"{truncatedSpans} page span(s) fell outside the analyzed content and were clamped; page text may be incomplete.",
                    blobName));

            // File-level counts of cosmetic normalization: worth knowing, not a defect,
            // and not worth one entry per page.
            var infos = new List<AnalysisWarning>(2);

            if (setextNormalized > 0)
                infos.Add(new AnalysisWarning(
                    "SetextTitleNormalized",
                    $"Setext-style title normalized to ATX on {setextNormalized} page(s).",
                    blobName));

            if (noiseStripped > 0)
                infos.Add(new AnalysisWarning(
                    "NoiseCommentsStripped",
                    $"{noiseStripped} DI decoration comment(s) (page header/footer/number/figure-content) stripped across {pagesWithNoise} page(s).",
                    blobName));

            return (pages, warnings, infos);
        }

        // Concatenates a page's spans in offset order.
        // - Offsets come from the service and index into Content; they are a trust boundary,
        //   so they're clamped rather than passed straight to Substring, where one malformed
        //   span would throw ArgumentOutOfRangeException past every typed error path.
        // - Fast paths: no spans, and the overwhelmingly common single-span page (no sort,
        //   no builder).
        private static string SliceBySpans(string content, IReadOnlyList<DocumentSpan>? spans, ref int truncated)
        {
            if (spans is not { Count: > 0 }) return "";

            if (spans.Count == 1)
                return Clamp(content, spans[0], ref truncated).ToString();

            var ordered = spans.OrderBy(s => s.Offset);
            var builder = new StringBuilder(content.Length < 8192 ? content.Length : 8192);

            foreach (var span in ordered)
                builder.Append(Clamp(content, span, ref truncated));

            return builder.ToString();

            static ReadOnlySpan<char> Clamp(string content, DocumentSpan span, ref int truncated)
            {
                if (span.Offset < 0 || span.Offset >= content.Length)
                {
                    truncated++;
                    return default;
                }

                var length = Math.Min(span.Length, content.Length - span.Offset);
                if (length < span.Length) truncated++;

                return content.AsSpan(span.Offset, length);
            }
        }

        // Matches a whole "<!-- PageHeader="..." -->"-style line (also PageFooter/PageNumber/
        // FigureContent), anchored to a full line so the same literal text appearing in the
        // document's own prose isn't eaten. The quoted value uses (?:[^"\\]|\\.)* rather than
        // a lazy ".*?" so an escaped quote inside it can't truncate the match early.
        [GeneratedRegex(
            @"^[ \t]*<!--\s*(?:Page(?:Header|Footer|Number)|FigureContent)\s*=\s*""(?:[^""\\]|\\.)*""\s*-->[ \t]*\r?\n?",
            RegexOptions.Multiline)]
        private static partial Regex NoiseCommentLineRegex();

        // DI renders the document Title as setext ("Title" + "===" underline) unlike every
        // other heading, which it renders as ATX.
        // - "=" underlines only: "-" underlines are ambiguous with a thematic break (<hr>).
        // - \r? before $ is load-bearing. .NET's multiline $ anchors immediately before \n,
        //   and [ \t]* can't consume a \r, so without it the pattern silently never matches
        //   CRLF content.
        // - The title group excludes \r as well as \n, so it can't swallow the line's own
        //   carriage return on CRLF input.
        [GeneratedRegex(@"^(?<title>[^\r\n]+)\r?\n=+[ \t]*\r?$", RegexOptions.Multiline)]
        private static partial Regex SetextTitleRegex();

        [GeneratedRegex(@"<table\b", RegexOptions.IgnoreCase)]
        private static partial Regex TableOpenTagRegex();

        [GeneratedRegex(@"</table\s*>", RegexOptions.IgnoreCase)]
        private static partial Regex TableCloseTagRegex();

        // --- Structure ------------------------------------------------------------

        // Paragraphs DI classified as real section structure, not incidental roles.
        // Offset/PageNumber come from Spans/BoundingRegions: DocumentParagraph has no
        // PageNumber of its own.
        private static IReadOnlyList<Heading> GetHeadings(AnalyzeResult result) =>
            result.Paragraphs
                .Where(p => p.Role == ParagraphRole.Title || p.Role == ParagraphRole.SectionHeading)
                .Select(ToHeading)
                .ToList();

        // Repeated page furniture, kept separate so "Headings" only ever means real structure.
        // PageNumber belongs here rather than in its own bucket; without it those paragraphs
        // fell through both and vanished.
        private static IReadOnlyList<Heading> GetBoilerplate(AnalyzeResult result) =>
            result.Paragraphs
                .Where(p => p.Role == ParagraphRole.PageHeader || p.Role == ParagraphRole.PageFooter
                         || p.Role == ParagraphRole.Footnote   || p.Role == ParagraphRole.PageNumber)
                .Select(ToHeading)
                .ToList();

        private static Heading ToHeading(DocumentParagraph p) => new(
            p.Content,
            p.Role.ToString()!,
            FirstOffset(p.Spans),
            FirstPage(p.BoundingRegions));

        // Offset is null, never 0, when there are no spans: 0 is a valid real offset and
        // can't double as "unknown".
        private static int? FirstOffset(IReadOnlyList<DocumentSpan>? spans) =>
            spans is { Count: > 0 } s ? s[0].Offset : null;

        private static int FirstPage(IReadOnlyList<BoundingRegion>? regions) =>
            regions is { Count: > 0 } r ? r[0].PageNumber : 0;

        // Each page's width/height/unit as DI measured it, not the PDF's own MediaBox.
        private static IReadOnlyList<PageDimensions> GetPageDimensions(AnalyzeResult result) =>
            result.Pages
                .Select(p => new PageDimensions(p.PageNumber, p.Width, p.Height, p.Unit.ToString() ?? ""))
                .ToList();

        // Every table, with cell position, kind (columnHeader vs content) and merge spans.
        // RowSpan/ColumnSpan are null for an ordinary cell; without them a merged header
        // cell looks like a missing cell downstream.
        // Caption/Footnotes/Regions are free fields off the same DocumentTable already in
        // hand - see TableInfo for why Regions captures every BoundingRegion rather than
        // just the first.
        private static IReadOnlyList<TableInfo> GetTables(AnalyzeResult result) =>
            result.Tables
                .Select(t => new TableInfo(
                    t.RowCount,
                    t.ColumnCount,
                    t.Cells.Select(c => new TableCellInfo(
                        c.RowIndex, c.ColumnIndex, c.Kind.ToString() ?? "", c.Content, c.RowSpan, c.ColumnSpan)).ToList(),
                    FirstOffset(t.Spans),
                    FirstPage(t.BoundingRegions),
                    t.Caption?.Content,
                    t.Footnotes.Select(f => f.Content).ToList(),
                    (t.BoundingRegions ?? []).Select(br => new DocumentRegion(br.PageNumber, ToPolygonPoints(br.Polygon))).ToList()))
                .ToList();

        // Every checkbox/radio: state, DI's confidence, bounding polygon.
        // Offset comes from Span (singular): a selection mark has exactly one position,
        // unlike paragraphs/tables.
        private static IReadOnlyList<SelectionMarkInfo> GetSelectionMarks(AnalyzeResult result) =>
            result.Pages
                .SelectMany(p => p.SelectionMarks.Select(sm => new SelectionMarkInfo(
                    p.PageNumber, sm.State.ToString(), sm.Span.Offset, sm.Confidence, ToPolygonPoints(sm.Polygon))))
                .ToList();

        // Every figure DI detected. All free under prebuilt-layout; no add-on required.
        // - Id: only needed to fetch the cropped image later via the figures endpoint.
        // - Elements: JSON-pointer refs to paragraphs discussing the figure, broader than Caption.
        private static IReadOnlyList<FigureInfo> GetFigures(AnalyzeResult result) =>
            result.Figures
                .Select(f => new FigureInfo(
                    f.Caption?.Content,
                    FirstOffset(f.Spans),
                    FirstPage(f.BoundingRegions),
                    f.Id,
                    f.Elements ?? []))
                .ToList();

        // Every OCR line with its polygon: the most granular positional data DI offers free.
        // - Future highlight-on-source join: a chunk's span range selects its lines by
        //   Offset, and their polygons union into the highlight region.
        // - By far the bulkiest structure here. Not persisted permanently today (dev reports
        //   only), which is correct until source-grounding ships.
        private static IReadOnlyList<LineInfo> GetLines(AnalyzeResult result) =>
            result.Pages
                .SelectMany(p => p.Lines.Select(line => new LineInfo(
                    line.Content, FirstOffset(line.Spans), p.PageNumber, ToPolygonPoints(line.Polygon))))
                .ToList();

        // DI returns polygons as a flat [x1, y1, x2, y2, ...] float list rather than typed
        // points; paired up here so callers don't have to know that.
        private static IReadOnlyList<PolygonPoint> ToPolygonPoints(IReadOnlyList<float>? polygon)
        {
            if (polygon is not { Count: > 1 }) return [];

            var points = new List<PolygonPoint>(polygon.Count / 2);
            for (var i = 0; i + 1 < polygon.Count; i += 2)
                points.Add(new PolygonPoint(polygon[i], polygon[i + 1]));
            return points;
        }

        // DI's own sections: the closest prebuilt-layout gets to semantic chunk boundaries,
        // vs the page-only boundaries GetPages relies on today.
        // - Every span kept (not anchor-only like the others): a section only means something
        //   as a start-to-end range.
        // - Elements stay as raw JSON-pointer strings; resolving them is a future
        //   chunk-builder's job.
        private static IReadOnlyList<SectionInfo> GetSections(AnalyzeResult result) =>
            result.Sections
                .Select(s => new SectionInfo(
                    s.Spans.Select(sp => new SectionSpan(sp.Offset, sp.Length)).ToList(),
                    s.Elements.ToList()))
                .ToList();

        // --- Quality --------------------------------------------------------------

        // Average OCR word-confidence scoring (PageQuality/LowPageConfidence) was removed
        // here: it measures DI's uncertainty about pixel-derived text, which doesn't apply
        // to this born-digital, no-scan corpus - DI decodes an embedded text stream with no
        // pixel ambiguity to be uncertain about, so the score never moved (sampled at
        // 0.94-0.995 across a real batch, nowhere near the 0.85 threshold it was compared
        // against). It also can't catch this corpus's actual failure mode - a broken
        // ToUnicode CMap decodes with full (wrong) confidence, since DI isn't uncertain
        // about what it read, just wrong about what it means. See
        // docs/260728/extraction-optimisation.md for the sampling this was based on, and
        // the deferred replacement signal (F) once one is needed.
        // ZeroWordsOnPage survives: unlike confidence, "DI found no words at all" is still
        // meaningful on a born-digital page - either genuinely blank or entirely vector
        // figure content.
        // internal (not private): testable without a live DI call, as with GetPages.
        internal static IReadOnlyList<AnalysisWarning> GetZeroWordWarnings(AnalyzeResult result, string blobName) =>
            result.Pages
                .Where(p => p.Words.Count == 0)
                .Select(p => new AnalysisWarning(
                    "ZeroWordsOnPage",
                    $"Page {p.PageNumber} has zero detected words - either genuinely blank or entirely vector figure content (no OCR-able text).",
                    blobName))
                .ToList();

        // File-level structural defects: things DI extracted but returned incomplete or
        // malformed. Computed from what GetTables/GetFigures already produced, so they're
        // flagged at the source rather than recomputed by PdfPipelineValidator later.
        // Cost is not here: it's data (DocumentAnalyzedResults.EstimatedCostUsd) and an
        // info entry, not a defect.
        // internal (not private): testable without a live DI call, as with GetPages.
        internal static IReadOnlyList<AnalysisWarning> StructureWarnings(
            IReadOnlyList<TableInfo> tables, IReadOnlyList<FigureInfo> figures, string blobName)
        {
            var warnings = new List<AnalysisWarning>(2);

            var uncaptioned = figures.Count(f => f.Caption is null);
            if (uncaptioned > 0)
                warnings.Add(new AnalysisWarning(
                    "FiguresWithoutCaption",
                    $"{uncaptioned} of {figures.Count} figure(s) have no caption.",
                    blobName));

            var malformed = tables.Count(t => t.Cells.Count == 0 || t.RowCount == 0 || t.ColumnCount == 0);
            if (malformed > 0)
                warnings.Add(new AnalysisWarning(
                    "MalformedTable",
                    $"{malformed} of {tables.Count} table(s) have no cells or a zero row/column count.",
                    blobName));

            return warnings;
        }

        // Cost echo: informational, not a defect, so it goes to Infos rather than Warnings.
        // Takes the already-computed estimatedCost rather than recomputing pageCount *
        // CostPerPage itself - DocumentAnalyzedResults.EstimatedCostUsd and this message
        // must agree, so there's exactly one place that does the multiplication.
        // internal (not private): keeps the message format unit-testable without a live
        // DI call, as with StructureWarnings.
        internal static AnalysisWarning CostInfo(decimal estimatedCost, int pageCount, string blobName) =>
            new("EstimatedCost",
                $"Estimated cost: ${estimatedCost:F2} ({pageCount} page(s) at ${CostPerPage}/page).",
                blobName);

        // DI's own non-fatal warnings (e.g. a page that partially failed OCR), distinct from
        // the zero-pages case which is an outright failure. Wraps the SDK type so callers
        // don't need it.
        private static IReadOnlyList<AnalysisWarning> GetDiWarnings(AnalyzeResult result) =>
            result.Warnings
                .Select(w => new AnalysisWarning(w.Code, w.Message, w.Target))
                .ToList();
    }
}
