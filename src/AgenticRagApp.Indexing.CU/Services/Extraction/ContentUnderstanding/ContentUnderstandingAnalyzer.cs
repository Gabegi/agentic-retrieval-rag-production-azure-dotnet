using Azure;
using Azure.AI.ContentUnderstanding;
using Microsoft.Extensions.Logging;
using AgenticRagApp.Infrastructure.Clients.ContentUnderstanding;
using AgenticRagApp.Infrastructure.Configuration;
using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

// The paid Content Understanding call for one document: submit, poll to completion, hand the
// response on.
//
// It does NOT vet what came back beyond asserting the string encoding - response validation was
// deliberately removed while the flow is being settled (CU response -> mapped output ->
// chunking). Downstream reads the response as given.
//
// Retry discipline lives in AnalysisPoller, which is generic over the result and outcome types
// and was written that way precisely so this class could reuse it unchanged - it retries only
// the free status poll on 429/5xx/transient network errors and never resubmits the paid POST.
public sealed class ContentUnderstandingAnalyzer
{
    // AnalysisPoller sizes its wall-clock budget per page (5 min base + 3 s/page), which the
    // Document Intelligence path fed from PdfPig's preflight page count. There is no preflight
    // any more - nothing opens the PDF locally before submitting - so there is no page count to
    // give it.
    //
    // 300 is Content Understanding's own async analysis cap, so it is the largest document that
    // can reach this call at all: budget = 5 min + 300x3 s = 20 min, comfortably inside the
    // 50-minute ceiling that keeps a run under Durable's activityFunctionTimeout. Sizing to the
    // cap rather than the file means a large document is never abandoned mid-analysis for a
    // budget that was set too small; the cost is that a 3-page document also waits 20 minutes
    // before giving up, which only matters on a service outage the poll retries already cover.
    private const int BudgetPageCount = 300;

    private readonly IContentAnalysisClient _client;
    private readonly IndexerConfig          _config;
    private readonly ILogger               _logger;

    // Backoff/poll-interval waits go through this instead of a bare Task.Delay so tests can
    // substitute an instant no-op - retry backoff is on real TimeSpan schedules (seconds), and
    // a test exercising the retry-exhaustion path would otherwise wait through it in wall-clock
    // time.
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public ContentUnderstandingAnalyzer(
        IContentAnalysisClient client, IndexerConfig config,
        ILogger<ContentUnderstandingAnalyzer> logger,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _client = client;
        _config = config;
        _logger = logger;
        _delay  = delay ?? Task.Delay;
    }

    public async Task<AnalyzeOutcome> AnalyzeAsync(byte[] bytes, string blobName, CancellationToken ct)
    {
        _logger.LogInformation(
            "Submitting '{Blob}' to Content Understanding analyzer '{AnalyzerId}'.",
            blobName, _config.ContentUnderstandingAnalyzerId);

        // The completed operation, captured so validation can read usage off it.
        //
        // Usage is NOT on AnalysisResult - it comes from AnalyzeOperationExtensions.GetUsage,
        // which takes the Operation. AnalysisPoller's validate delegate only receives the
        // result, and widening that contract would mean changing a class shared with nothing
        // else just to thread one value. Capturing here is the smaller move, and it is safe:
        // validate runs only after the poller has seen HasCompleted.
        Operation<AnalysisResult>? operation = null;

        var poll = await AnalysisPoller.SubmitAndPollAsync<AnalysisResult, AnalyzeOutcome>(
            submit: async pollCt => operation = await _client.SubmitAnalyzeAsync(
                bytes, _config.ContentUnderstandingAnalyzerId, range: null, ct: pollCt),
            logger:    _logger,
            delay:     _delay,
            blobName:  blobName,
            pageCount: BudgetPageCount,
            ct:        ct,
            validate:  (result, name) => ValidateAnalyzeResult(result, name, operation),
            fail:      AnalyzeOutcome.Fail);

        if (poll.ThrottleRetries > 0)
            _logger.LogWarning(
                "Content Understanding analysis of '{Blob}' needed {Retries} throttled poll retry/retries.",
                blobName, poll.ThrottleRetries);

        return poll.Outcome;
    }

    // Deliberately down to a single check.
    //
    // Response validation (exactly-one-document, non-empty markdown, no YAML front matter, at
    // least one page) and the non-BMP tripwire were removed: the flow is CU response -> mapped
    // output -> chunking, and what a bad response should do about it is an open question we are
    // not answering yet. Whatever replaces them goes here.
    //
    // The utf16 assertion stays because it is not about this document - it verifies that
    // Utf16StringEncodingPolicy actually took effect, which is a deploy-level fact every
    // structural Offset in the pipeline depends on, and it fails silently and globally when
    // wrong. See the comment below.
    // internal (not private): unit tested directly against a hand-built AnalysisResult.
    internal AnalyzeOutcome ValidateAnalyzeResult(
        AnalysisResult result, string blobName, Operation<AnalysisResult>? operation)
    {
        // The utf16 assertion. Every structural Offset and every span this pipeline reads is
        // a UTF-16 code-unit index, because that is what a C# string indexes by and what
        // Substring expects. Content Understanding defaults spans to Unicode CODE POINTS; we
        // ask for utf16 through a pipeline policy (Utf16StringEncodingPolicy) because the
        // SDK has no typed parameter for it, and a query parameter set that way can be
        // silently ignored. StringEncoding echoes what the service actually applied, so this
        // is the difference between "the parameter arrived" and "every offset in this
        // response is quietly wrong on any non-BMP character".
        if (!string.Equals(result.StringEncoding, "utf16", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError(
                "Content Understanding returned stringEncoding '{Actual}' for '{Blob}', expected 'utf16'. " +
                "Every structural Offset would be a code-point index and would drift on any non-BMP character. " +
                "Check Utf16StringEncodingPolicy - it only matches ':analyze', so the LRO result URL may need it too.",
                result.StringEncoding, blobName);

            return AnalyzeOutcome.Fail(blobName,
                $"Content Understanding returned stringEncoding '{result.StringEncoding}', expected 'utf16'.",
                PdfOpenFailureReason.UnexpectedContentFormat);
        }

        return new AnalyzeOutcome(true, result, null)
        {
            // The service's own warnings, passed through. Not a check of ours - it reports what
            // Content Understanding already said about the document.
            Warnings = [.. ServiceWarnings(result, blobName)],
            Usage    = ReadUsage(operation, blobName),
        };
    }

    // Reading usage must never be the thing that fails a successful, already-billed analysis -
    // it is a reporting value, and the SDK extension can throw on an operation shape we did not
    // anticipate.
    private AnalyzeUsageDetails? ReadUsage(Operation<AnalysisResult>? operation, string blobName)
    {
        if (operation is null) return null;

        try
        {
            return operation.GetUsage();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not read Content Understanding usage details for '{Blob}'; cost reporting will under-count this run.",
                blobName);
            return null;
        }
    }

    private static IReadOnlyList<AnalysisWarning> ServiceWarnings(AnalysisResult result, string blobName) =>
        [.. (result.Warnings ?? []).Select(w => new AnalysisWarning(w.Code ?? "", w.Message ?? "", blobName))];
}
