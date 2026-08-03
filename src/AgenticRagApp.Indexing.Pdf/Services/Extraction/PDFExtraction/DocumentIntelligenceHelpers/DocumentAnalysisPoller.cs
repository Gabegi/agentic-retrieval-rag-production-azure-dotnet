using System.Globalization;
using Azure;
using Azure.AI.DocumentIntelligence;
using Microsoft.Extensions.Logging;
using AgenticRagApp.Infrastructure.Clients.DocumentIntelligence;
using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Submit + poll cycle for one Document Intelligence analyze call, including its
// throttling/retry/backoff behavior. Split out of PdfDocumentIntelligenceAnalyzer so that
// class stays focused on orchestration; every dependency (diClient/logger/delay) is passed
// in explicitly rather than captured from an instance.
internal static class DocumentAnalysisPoller
{
    // Backoff after *consecutive* retryable failures on the status poll (429, retryable
    // 5xx, transient network errors - see IsRetryablePollFailure), per Microsoft's
    // documented 2-5-13-34 pattern. A Retry-After header, when present and the failure
    // was a 429, wins over this schedule.
    private static readonly TimeSpan[] BackoffDelays =
    [
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(13), TimeSpan.FromSeconds(34)
    ];

    // Ordinary interval between status-poll GETs; unrelated to BackoffDelays, which
    // only applies after a retryable poll failure. Microsoft advises no more than one
    // poll per 2s.
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(2);

    // Hard ceiling on one document's analysis, so a server-side operation that never
    // reports completion can't pin a worker forever. Only applies when the caller's
    // own token has no earlier deadline; see AnalyzeDocumentAsync.
    // Scaled by page count rather than flat: preflight admits up to 2,000 pages, and a
    // flat ceiling generous enough for those would be useless on a 3 page file, while
    // one tight enough for small files abandons large ones mid-analysis. Abandoning
    // costs real money here, since the analyze POST is already billed by the time this
    // fires. Budget is deliberately loose; it's a runaway guard, not an SLA.
    //
    // Ceiling clamped below host.json's durableTask.activityFunctionTimeout (60 minutes,
    // ExtractActivity runs every document's analysis inside that one activity call) -
    // at 90 minutes this guard could never fire before Durable's own timeout does, and a
    // Durable timeout redelivers the WHOLE activity (see PdfIndexingFunction's ExtractActivity
    // comment), re-submitting and re-billing every already-completed analysis in this run,
    // not just the one that was still in flight. 50 minutes leaves headroom under the
    // 60-minute ceiling for cleaning/validation/blob writes after the last analysis
    // completes (finding #11).
    private static readonly TimeSpan AnalyzeBudgetBase    = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AnalyzeBudgetPerPage = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan AnalyzeBudgetCeiling = TimeSpan.FromMinutes(50);

    // PageCount comes from PdfPig, which counts every page in the file. DI bills and
    // analyzes the same pages for PDFs, so the two agree here. Falls back to the flat
    // base if preflight somehow reported nothing usable.
    private static TimeSpan AnalyzeBudget(int pageCount)
    {
        if (pageCount <= 0) return AnalyzeBudgetBase;

        var budget = AnalyzeBudgetBase + (AnalyzeBudgetPerPage * pageCount);
        return budget > AnalyzeBudgetCeiling ? AnalyzeBudgetCeiling : budget;
    }

    internal readonly record struct PollResult(AnalyzeOutcome Outcome, int ThrottleRetries);

    // Submits once (WaitUntil.Started), then polls manually instead of using
    // WaitUntil.Completed.
    // - Why: control over the poll, not cost. The paid unit is the analyze POST; every
    //   UpdateStatus after it is a GET against Operation-Location and is free, and
    //   Azure.Core retries the individual failed request, so a 429 while polling
    //   retries the status check under WaitUntil.Completed too. What WaitUntil.Completed
    //   doesn't give us is a usable backoff: per Azure/azure-sdk-for-net#50904 its
    //   internal loop can still hit 429 with client-level retry configured, and it
    //   offers no hook for Retry-After, a consecutive-failure budget, or a wall-clock
    //   ceiling. Sustained throttling here costs time and a possible timeout, not pages.
    // - A poll is a free GET against an already-paid analysis, so 429s, retryable 5xx,
    //   and transient network errors (HttpRequestException/IOException) are all worth
    //   retrying here rather than abandoning a billed document - see
    //   IsRetryablePollFailure.
    // - Backoff is indexed by *consecutive* retryable failures, not by poll count.
    //   Indexing by poll count silently spent the whole retry budget on successful
    //   polls, so any document taking more than BackoffDelays.Length * PollingInterval
    //   to analyze had zero retries left by the time a failure could occur.
    // - Retry-After, when the service sends it on a 429, is authoritative over
    //   BackoffDelays.
    // - OperationCanceledException from the caller's own token propagates (host
    //   shutdown, not a per-document failure). The internal timeout becomes a typed error.
    // validateAnalyzeResult stays a delegate into PdfDocumentIntelligenceAnalyzer rather
    // than moving here too: it's still an instance method there (uses _logger), extraction
    // for a future cluster.
    public static async Task<PollResult> SubmitAndPollAsync(
        IDocumentAnalysisClient diClient, ILogger logger, Func<TimeSpan, CancellationToken, Task> delay,
        string blobName, AnalyzeDocumentOptions options, int pageCount, CancellationToken ct,
        Func<AnalyzeResult, string, AnalyzeOutcome> validateAnalyzeResult)
    {
        var budget = AnalyzeBudget(pageCount);

        // Linked so the caller's token still wins if it fires first; CancelAfter only
        // adds a ceiling, it never extends the caller's deadline.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(budget);
        var pollCt = timeoutCts.Token;

        var throttleRetries = 0;

        try
        {
            // A rejected submit (429/503) never got billed - Azure returned no operation,
            // no pages were analyzed - so unlike an already-billed operation, retrying it
            // costs nothing extra. Only 429/503 retry here (not the wider 500/502/504 set
            // IsRetryablePollFailure allows for the free status poll): those two are the
            // unambiguous "rejected before any work happened" cases; a bare 500 on submit
            // is not worth the same assumption. Same backoff schedule and Retry-After
            // precedence as polling, with its own consecutive-failure budget so a
            // sustained outage still gives up rather than retrying forever.
            Operation<AnalyzeResult> operation;
            var submitRetries = 0;
            while (true)
            {
                try
                {
                    operation = await diClient.SubmitAnalyzeAsync(options, pollCt);
                    break;
                }
                catch (RequestFailedException ex) when (ex.Status is 429 or 503 && submitRetries < BackoffDelays.Length)
                {
                    var wait = RetryAfter(ex) ?? BackoffDelays[submitRetries];
                    submitRetries++;
                    if (ex.Status == 429) throttleRetries++;

                    logger.LogWarning(ex,
                        "Document Intelligence rejected the analyze submission for '{Blob}' (status {Status}, consecutive #{Count}); " +
                        "backing off {Wait} before resubmitting - not previously billed.",
                        blobName, ex.Status, submitRetries, wait);

                    await delay(wait, pollCt);
                }
                catch (RequestFailedException ex)
                {
                    logger.LogWarning(ex, "Document Intelligence rejected the analyze submission for '{Blob}'.", blobName);
                    return new PollResult(RequestFailure(blobName, ex), throttleRetries);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Unexpected error submitting '{Blob}' to Document Intelligence.", blobName);
                    return new PollResult(Unexpected(blobName, ex), throttleRetries);
                }
            }

            // Consecutive-failure counter: reset by any successful poll, so a document
            // that is throttled/blips, recovers, then fails again gets a fresh budget.
            var consecutiveFailures = 0;

            while (!operation.HasCompleted)
            {
                try
                {
                    await operation.UpdateStatusAsync(pollCt);
                    consecutiveFailures = 0;
                }
                catch (Exception ex) when (IsRetryablePollFailure(ex) && consecutiveFailures < BackoffDelays.Length)
                {
                    var wait = (ex as RequestFailedException) is { } rfe
                        ? RetryAfter(rfe) ?? BackoffDelays[consecutiveFailures]
                        : BackoffDelays[consecutiveFailures];

                    consecutiveFailures++;
                    if ((ex as RequestFailedException)?.Status == 429) throttleRetries++;

                    logger.LogWarning(ex,
                        "Transient failure polling '{Blob}' (consecutive #{Count}, status {Status}); backing off {Wait}.",
                        blobName, consecutiveFailures, (ex as RequestFailedException)?.Status, wait);

                    await delay(wait, pollCt);
                    continue;
                }
                catch (RequestFailedException ex)
                {
                    logger.LogWarning(ex,
                        "Document Intelligence failed while polling '{Blob}' (status {Status}) after {Count} consecutive transient failure(s).",
                        blobName, ex.Status, consecutiveFailures);

                    return new PollResult(RequestFailure(blobName, ex), throttleRetries);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Unexpected error polling '{Blob}' with Document Intelligence.", blobName);
                    return new PollResult(Unexpected(blobName, ex), throttleRetries);
                }

                if (!operation.HasCompleted)
                    await delay(PollingInterval, pollCt);
            }

            // Guard before .Value: an LRO that completes *without* a value throws
            // InvalidOperationException from Value (Azure/azure-sdk-for-net#27516), which
            // no handler here would catch and no typed error would describe. A non-success
            // completion normally surfaces as RequestFailedException from UpdateStatusAsync
            // and is handled above; this covers the remaining case.
            if (!operation.HasValue)
            {
                logger.LogWarning("Document Intelligence completed '{Blob}' with no result value.", blobName);
                return new PollResult(
                    Fail(blobName,
                        "Document Intelligence operation completed without a result.",
                        PdfOpenFailureReason.MissingAnalysisResult),
                    throttleRetries);
            }

            return new PollResult(validateAnalyzeResult(operation.Value, blobName), throttleRetries);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our ceiling fired, not the caller's token: a per-document failure, not a shutdown.
            logger.LogWarning(
                "Document Intelligence analysis of '{Blob}' exceeded its {Limit} budget ({Pages} page(s)); abandoning a paid analysis.",
                blobName, budget, pageCount);
            return new PollResult(
                Fail(blobName,
                    $"Document Intelligence analysis timed out after {budget.TotalMinutes:F0} minute(s) for {pageCount} page(s).",
                    PdfOpenFailureReason.DiServiceError),
                throttleRetries);
        }
    }

    // A poll is a free GET against a paid, already-running analysis, so anything
    // plausibly transient is worth another attempt: abandoning here throws away work
    // that has already been billed. 429 plus the standard retryable 5xx, plus the
    // network-level exceptions the SDK surfaces raw.
    // OperationCanceledException is excluded deliberately: cancellation is either the
    // caller's shutdown or our own AnalyzeBudget ceiling, and both are handled
    // outside this loop.
    // internal (not private): unit tested directly with hand-built exceptions.
    internal static bool IsRetryablePollFailure(Exception ex) => ex switch
    {
        OperationCanceledException                                     => false,
        RequestFailedException { Status: 429 or 500 or 502 or 503 or 504 } => true,
        System.Net.Http.HttpRequestException                           => true,
        IOException                                                    => true,
        _                                                               => false,
    };

    // Floor for a Retry-After-derived delay: a zero-second wait on a 429 is
    // effectively no backoff at all, so a past date or a zero/negative delta still
    // waits at least this long rather than retrying immediately.
    private static readonly TimeSpan MinRetryAfter = TimeSpan.FromSeconds(1);

    // Retry-After is the service's own instruction and beats any fixed schedule.
    // Sent either as delta-seconds or as an HTTP-date; both are accepted here.
    // internal (not private): unit tested directly against a RequestFailedException
    // carrying a controllable raw response header.
    internal static TimeSpan? RetryAfter(RequestFailedException ex)
    {
        if (ex.GetRawResponse() is not { } response ||
            !response.Headers.TryGetValue("Retry-After", out var raw) ||
            string.IsNullOrWhiteSpace(raw))
            return null;

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            return seconds > 0 ? TimeSpan.FromSeconds(seconds) : MinRetryAfter;

        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var when))
        {
            var delay = when - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : MinRetryAfter;
        }

        return null;
    }

    // --- Typed failures -------------------------------------------------------

    // RowNumber is left null: a PDF failure is file-level, not row-addressable. It used to
    // be forced to 0 here because the old ExtractionError's RowNumber was non-nullable and
    // shared with the CSV extractor, so 0 was the only available "not applicable" value -
    // indistinguishable from a genuine row 0. PipelineIssue.RowNumber is int?, so the
    // distinction is now expressible and 0 no longer has to stand in for absent.
    // internal (not private): shared with PdfDocumentIntelligenceAnalyzer.ValidateAnalyzeResult.
    internal static AnalyzeOutcome Fail(string blobName, string message, PdfOpenFailureReason reason) =>
        new(false, null, PipelineIssue.Error(
            PipelineStage.ParsePages,
            blobName,
            message,
            reason: reason));

    private static AnalyzeOutcome RequestFailure(string blobName, RequestFailedException ex) =>
        Fail(blobName,
            $"Document Intelligence request failed ({ex.Status}): {ex.Message}",
            ex.Status == 429 ? PdfOpenFailureReason.Throttled : PdfOpenFailureReason.DiServiceError);

    private static AnalyzeOutcome Unexpected(string blobName, Exception ex) =>
        Fail(blobName,
            $"Document Intelligence analysis failed unexpectedly: {ex.Message}",
            PdfOpenFailureReason.Unknown);
}
