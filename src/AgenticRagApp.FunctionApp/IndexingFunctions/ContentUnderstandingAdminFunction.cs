using System.Net;
using System.ClientModel.Primitives;
using System.Text.Json;
using Azure.AI.ContentUnderstanding;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using AgenticRagApp.Infrastructure.Clients.Blob;
using AgenticRagApp.Infrastructure.Clients.ContentUnderstanding;
using AgenticRagApp.Infrastructure.Configuration;

namespace AgenticRagApp.Functions;

// Manually-triggered Content Understanding operator tooling - not part of any orchestration, in
// the same spirit as IndexAdminFunction next door.
//
// ONE WRITER PER OBJECT still holds, but the writer is no longer Terraform: cap-pdf-layout is
// owned by ContentUnderstandingProvisioner behind /provision below (decided 2026-08-21,
// docs/2608/260821/cu-provisioning-ownership.md; infra/content_understanding.tf carries the
// reasoning for why the application owns it and Terraform does not). What that rule forbids here
// is a SECOND writer - so /verify and /smoke stay strictly get-and-verify, and a missing or
// drifted analyzer is reported by them and repaired only by /provision.
//
// The account-wide default model-deployment mapping is the one piece of shared, merge-patched,
// account-global state; it is written by /provision as its first step, and opt-in via
// ?setDefaults=true on /verify.
public class ContentUnderstandingAdminFunction
{
    // These responses are read by a human in a terminal or by a pipeline step, so indent them.
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IContentUnderstandingVerifier    _verifier;
    private readonly IContentUnderstandingProvisioner _provisioner;
    private readonly IContentAnalysisClient           _analysisClient;
    private readonly IBlobStore                       _blobStore;
    private readonly BlobServiceClient                _blobServiceClient;
    private readonly IndexerConfig                    _config;
    private readonly ILogger<ContentUnderstandingAdminFunction> _logger;

    public ContentUnderstandingAdminFunction(
        IContentUnderstandingVerifier    verifier,
        IContentUnderstandingProvisioner provisioner,
        IContentAnalysisClient           analysisClient,
        IBlobStore                       blobStore,
        BlobServiceClient                blobServiceClient,
        IndexerConfig                    config,
        ILogger<ContentUnderstandingAdminFunction> logger)
    {
        _verifier          = verifier;
        _provisioner       = provisioner;
        _analysisClient    = analysisClient;
        _blobStore         = blobStore;
        _blobServiceClient = blobServiceClient;
        _config            = config;
        _logger            = logger;
    }

    // Creates the analyzer and the account defaults, in that order, and reports what the account
    // actually holds afterwards. Idempotent: an analyzer that already verifies clean is left
    // alone, so this belongs at the end of every Deploy stage rather than in someone's runbook.
    //
    // ?replace=true forces the analyzer to be rewritten even when it verifies clean. Not needed
    // for drift - a drifted analyzer is replaced anyway, since the alternative is a deploy step
    // that reports success against an analyzer known to be wrong.
    //
    // 200 when the analyzer is Ready and verifies clean, 500 otherwise - so a pipeline step can
    // just check the status code.
    [Function("ProvisionContentUnderstanding")]
    public async Task<HttpResponseData> RunProvision(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "content-understanding/provision")] HttpRequestData req,
        FunctionContext context)
    {
        var replace = string.Equals(req.Query["replace"], "true", StringComparison.OrdinalIgnoreCase);
        var ct      = context.CancellationToken;

        _logger.LogInformation(
            "ProvisionContentUnderstanding triggered for analyzer '{AnalyzerId}' (replace={Replace}).",
            _config.ContentUnderstandingAnalyzerId, replace);

        try
        {
            var result = await _provisioner.ProvisionAsync(replace, ct);

            var response = req.CreateResponse(result.Ok ? HttpStatusCode.OK : HttpStatusCode.InternalServerError);
            await response.WriteStringAsync(JsonSerializer.Serialize(new
            {
                ok             = result.Ok,
                analyzerId     = result.AnalyzerId,
                action         = result.AnalyzerAction.ToString(),
                analyzerStatus = result.AnalyzerStatus?.ToString(),
                defaults       = result.Defaults,
                problems       = result.Verification.Problems,
            }, JsonOptions));

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Content Understanding provisioning failed.");

            var response = req.CreateResponse(HttpStatusCode.InternalServerError);
            await response.WriteStringAsync(JsonSerializer.Serialize(new
            {
                ok      = false,
                error   = ex.Message,
            }, JsonOptions));

            return response;
        }
    }

    // Drift detector for cap-pdf-layout, plus the account defaults.
    //
    // GET-only by default. ?setDefaults=true additionally merge-PATCHes the account-wide model
    // deployment mapping - the one write Terraform delegates here, deliberately opt-in because it
    // is shared state and because the analyzer already pins its own models.completion, which makes
    // skipping it safe.
    [Function("VerifyContentUnderstanding")]
    public async Task<HttpResponseData> RunVerify(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "content-understanding/verify")] HttpRequestData req,
        FunctionContext context)
    {
        var setDefaults = string.Equals(req.Query["setDefaults"], "true", StringComparison.OrdinalIgnoreCase);
        var ct          = context.CancellationToken;

        _logger.LogInformation(
            "VerifyContentUnderstanding triggered for analyzer '{AnalyzerId}' (setDefaults={SetDefaults}).",
            _config.ContentUnderstandingAnalyzerId, setDefaults);

        try
        {
            var verification = await _verifier.VerifyAnalyzerAsync(ct);

            var defaults = setDefaults
                ? await _verifier.EnsureDefaultsAsync(ct)
                : await _verifier.GetDefaultsAsync(ct);

            var response = req.CreateResponse(verification.Ok ? HttpStatusCode.OK : HttpStatusCode.InternalServerError);
            await response.WriteStringAsync(JsonSerializer.Serialize(new
            {
                ok = verification.Ok,
                analyzer = new
                {
                    id             = verification.AnalyzerId,
                    exists         = verification.Exists,
                    status         = verification.Status?.ToString(),
                    baseAnalyzerId = verification.BaseAnalyzerId,
                    models         = verification.Models,
                    problems       = verification.Problems,
                },
                modelDeployments   = defaults,
                defaultsWereWritten = setDefaults,
                owner = "cap-pdf-layout is defined by ContentUnderstandingProvisioner and written by " +
                        "POST /api/content-understanding/provision. This endpoint only reads it.",
                nextStep = verification.Ok
                    ? "POST /api/content-understanding/smoke?blob=<name> to confirm a real analyze call."
                    : "POST /api/content-understanding/provision to create or repair the analyzer, then re-verify.",
            }, JsonOptions));
            response.Headers.Add("Content-Type", "application/json");
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "VerifyContentUnderstanding failed.");
            var failed = req.CreateResponse(HttpStatusCode.InternalServerError);
            await failed.WriteStringAsync($"Verification failed: {ex.Message}");
            return failed;
        }
    }

    // One document, end to end, through the real client against the real service.
    //
    // This is the only thing that proves the pieces this milestone added actually work together -
    // the utf16 pipeline policy in particular, which cannot be verified any other way: it either
    // reached the service or it did not, and the difference is invisible until offsets drift.
    //
    // It also produces the artifact the analyzer port needs. Every existing extraction-helper test
    // reads a captured Document Intelligence response; there is no Content Understanding equivalent
    // in the repo, and there cannot be one until a real call is made.
    [Function("SmokeContentUnderstanding")]
    public async Task<HttpResponseData> RunSmoke(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "content-understanding/smoke")] HttpRequestData req,
        FunctionContext context)
    {
        var blobName = req.Query["blob"];
        if (string.IsNullOrWhiteSpace(blobName))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Pass ?blob=<name> - the document to analyze, from the documents container.");
            return bad;
        }

        var ct        = context.CancellationToken;
        var documents = _blobServiceClient.GetBlobContainerClient("documents");

        try
        {
            var bytes = await _blobStore.DownloadBytesAsync(documents, blobName, ct);

            // Every call here bills a full analysis at CU's per-page rate, so the size is logged
            // before the submit rather than after: a repeated smoke against a large document is a
            // real line on the invoice, and this is the one place it is cheap to notice.
            _logger.LogWarning(
                "SmokeContentUnderstanding submitting '{Blob}' ({Bytes:N0} bytes) to analyzer '{AnalyzerId}' - this is a BILLED analysis.",
                blobName, bytes.Length, _config.ContentUnderstandingAnalyzerId);

            // WaitUntil.Started plus the SDK's own wait, rather than AnalysisPoller: this is a
            // one-off operator call outside any Durable activity, so none of the poller's
            // redelivery and budget machinery applies. The pipeline path uses AnalysisPoller.
            var operation = await _analysisClient.SubmitAnalyzeAsync(
                bytes, _config.ContentUnderstandingAnalyzerId, range: null, ct: ct);
            await operation.WaitForCompletionAsync(ct);

            var result   = operation.Value;
            var document = result.Contents.OfType<DocumentContent>().FirstOrDefault();

            // Persist the raw response before asserting anything about it. If a check below fails,
            // the JSON is what tells you why - and re-running to get it back costs another billed
            // analysis.
            var artifactPath = $"content-understanding-smoke/{Path.GetFileNameWithoutExtension(blobName)}.json";
            var artifacts    = _blobServiceClient.GetBlobContainerClient("pipeline-artifacts");
            await _blobStore.UploadAsync(
                artifacts, artifactPath,
                new BinaryData(ModelReaderWriter.Write(result).ToArray()), overwrite: true, ct);

            // The assertion the utf16 policy exists for. AnalysisResult.StringEncoding echoes what
            // the service actually applied, so this distinguishes "the query parameter arrived"
            // from "the query parameter was silently ignored" - which is the entire risk of
            // setting it through a pipeline policy rather than a typed parameter.
            var encodingOk = string.Equals(result.StringEncoding, "utf16", StringComparison.OrdinalIgnoreCase);
            if (!encodingOk)
                _logger.LogError(
                    "Content Understanding returned stringEncoding '{Actual}', expected 'utf16'. Every structural Offset " +
                    "would be a code-point index, not a UTF-16 one, and would drift on any non-BMP character. " +
                    "Check whether the LRO result URL needs the parameter too - Utf16StringEncodingPolicy only matches ':analyze'.",
                    result.StringEncoding);

            var figures         = document?.Figures ?? [];
            var figuresWithText = figures.Count(f => !string.IsNullOrWhiteSpace(f.Description));

            var response = req.CreateResponse(encodingOk ? HttpStatusCode.OK : HttpStatusCode.InternalServerError);
            await response.WriteStringAsync(JsonSerializer.Serialize(new
            {
                ok = encodingOk,
                blob = blobName,
                analyzerId     = result.AnalyzerId,
                apiVersion     = result.ApiVersion,
                stringEncoding = result.StringEncoding,
                expectedStringEncoding = "utf16",
                pages = new
                {
                    start = document?.StartPageNumber,
                    end   = document?.EndPageNumber,
                    count = document?.Pages?.Count ?? 0,
                },
                // Each of these being non-empty is what proves a config flag took effect:
                // paragraphs <- enableOcr + returnDetails, the rest <- enableLayout + returnDetails.
                // An empty list here means a successful, fully billed response that every
                // extraction helper would read as an empty document.
                details = new
                {
                    markdownLength = document?.Markdown?.Length ?? 0,
                    paragraphs     = document?.Paragraphs?.Count ?? 0,
                    sections       = document?.Sections?.Count   ?? 0,
                    tables         = document?.Tables?.Count     ?? 0,
                    figures        = figures.Count,
                    figuresWithDescription = figuresWithText,
                },
                serviceWarnings = result.Warnings?.Select(w => $"{w.Code}: {w.Message}").ToArray() ?? [],
                artifact = $"pipeline-artifacts/{artifactPath}",
            }, JsonOptions));
            response.Headers.Add("Content-Type", "application/json");
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SmokeContentUnderstanding failed for '{Blob}'.", blobName);
            var failed = req.CreateResponse(HttpStatusCode.InternalServerError);
            await failed.WriteStringAsync($"Smoke analyze failed for '{blobName}': {ex.Message}");
            return failed;
        }
    }
}
