using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using AgenticRagApp.Infrastructure.Clients.Blob;
using AgenticRagApp.Observability;
using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Functions.ReportEmail;

public sealed record SendReportEmailRequest(RunReportKind Kind, string InstanceId, DateTimeOffset StartedAt);

// Called by IndexingOrchestrator/RestoreOrchestrator immediately after
// SaveIndexReportActivity/SaveRestoreReportActivity write the run report. One run -> one email.
//
// Deliberately an activity, not a blob-triggered function. An earlier version of this feature
// fired off Event Grid watching pipeline-reports/runs/ - that meant a cross-account trigger
// (the data account, not the function's own AzureWebJobsStorage), a subject filter that could
// drift, a path guard to compensate, and a daily reconciliation timer whose entire job was
// detecting that the trigger had silently stopped firing. As an activity, the orchestrator
// already knows a run finished and already knows its instance ID - none of that machinery has
// anything left to do. See docs/2608/260807/pipeline-run-email-report.md for the run this
// design has already been through.
//
// Every step here is best-effort against the orchestration succeeding: this activity must never
// fail a good indexing run over a mail problem, so it catches broadly and always returns rather
// than throwing. Call it with a Durable retry policy (a few attempts) for transient failures
// inside those calls that are worth one retry - see the orchestrator's TaskOptions on this call.
public class SendReportEmailActivity
{
    // Kept for the delta section, exactly as it was under the trigger design: a pointer blob,
    // not a folder walk. Indexing runs are infrequent, so the previous run is usually days back;
    // "look in today's folder" loses the delta on the first run of every day, which is most runs.
    public const string LastRunPointerPath = RunReportAssembler.LastRunPointerPath;

    private static readonly JsonSerializerOptions s_summaryJson = new()
    {
        WriteIndented = true,
        Converters    = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IBlobStore          _blobStore;
    private readonly BlobContainerClient _reports;
    private readonly RunReportAssembler  _assembler;
    private readonly RunAnalysisAgent    _analyst;
    private readonly RunEmailRenderer    _renderer;
    private readonly IReportEmailSender  _sender;
    private readonly ReportEmailOptions  _options;
    private readonly ILogger<SendReportEmailActivity> _logger;

    // Takes BlobServiceClient and derives the container itself, rather than a plain
    // BlobContainerClient constructor parameter (which is what this had originally, and which
    // is never resolvable). The Functions Worker isolated-worker host does not activate
    // [Function] classes via serviceProvider.GetService(functionType) - it always calls
    // ActivatorUtilities.CreateInstance(scopedProvider, functionType), which resolves each
    // CONSTRUCTOR PARAMETER type directly from the container and completely ignores any
    // registration made for the class itself (e.g. services.AddSingleton<SendReportEmailActivity>
    // would be dead code here - confirmed in production 2026-08-07, the exact same
    // "Unable to resolve service for type BlobContainerClient" error survived that registration
    // unchanged). No unkeyed BlobContainerClient is registered anywhere in this app - every
    // other consumer of "pipeline-reports" builds one from BlobServiceClient inside its own
    // factory closure (see Program.cs's RunReportAssembler/RunReportWriter/etc. registrations);
    // this constructor now does the equivalent internally instead of asking the DI container
    // for a type nothing ever supplies.
    public SendReportEmailActivity(
        IBlobStore blobStore,
        BlobServiceClient blobServiceClient,
        RunReportAssembler assembler,
        RunAnalysisAgent analyst,
        RunEmailRenderer renderer,
        IReportEmailSender sender,
        ReportEmailOptions options,
        ILogger<SendReportEmailActivity> logger)
    {
        _blobStore = blobStore;
        _reports   = blobServiceClient.GetBlobContainerClient("pipeline-reports");
        _assembler = assembler;
        _analyst   = analyst;
        _renderer  = renderer;
        _sender    = sender;
        _options   = options;
        _logger    = logger;
    }

    [Function("SendReportEmailActivity")]
    public async Task Run([ActivityTrigger] SendReportEmailRequest req, FunctionContext context)
    {
        var ct = context.CancellationToken;

        if (!_options.Enabled)
        {
            _logger.LogInformation("Run report email disabled (ReportEmail:Enabled=false) — skipping {InstanceId}", req.InstanceId);
            return;
        }
        if (_options.RecipientList.Count == 0)
        {
            _logger.LogInformation("Run report email has no recipients configured — skipping {InstanceId}", req.InstanceId);
            return;
        }

        try
        {
            var path = req.Kind == RunReportKind.Restore
                ? RunReportRef.Restore(req.InstanceId, req.StartedAt)
                : RunReportRef.Index(req.InstanceId, req.StartedAt);
            var blobName = RunReportPath.Build(req.Kind, req.StartedAt, req.InstanceId);

            var summary = await _assembler.AssembleAsync(path, blobName, ct);
            if (summary is null)
            {
                _logger.LogError("Could not read run report '{Blob}' — no email sent", blobName);
                Instrumentation.PipelineFailures.Add(1, new KeyValuePair<string, object?>("stage", "report_email_assemble"));
                return;
            }

            // Never fails the send - a model outage degrades to "assessment unavailable".
            summary = summary with { Assessment = await _analyst.AnalyseAsync(summary, ct) };

            var (attachments, note) = await BuildAttachmentAsync(summary, ct);
            var subject = _renderer.RenderSubject(summary);
            var html    = _renderer.RenderHtml(summary, note);

            var sent = await _sender.SendAsync(subject, html, attachments, ct);
            if (!sent)
            {
                // Logged as an error and left there. There is no redelivery to protect against
                // here (unlike the old Event Grid design), so nothing gates a retry - Durable's
                // own retry policy on this activity call is what handles a transient failure.
                // A failure that survives those retries is a real gap; it shows up as this error
                // in the same App Insights the rest of the pipeline already reports to.
                _logger.LogError("Run report email failed to send for {InstanceId}", req.InstanceId);
                Instrumentation.PipelineFailures.Add(1, new KeyValuePair<string, object?>("stage", "report_email_send"));
                return;
            }

            await WriteLastRunPointerAsync(summary, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The indexing run itself already succeeded by the time this activity runs - a mail
            // problem must never surface as a failed indexing run.
            _logger.LogError(ex, "Run report email failed unexpectedly for {InstanceId}", req.InstanceId);
            Instrumentation.PipelineFailures.Add(1, new KeyValuePair<string, object?>("stage", "report_email_unhandled"));
        }
    }

    // The full assembled summary, attached so the run can be handed to an agent wholesale. Over
    // budget it goes to a blob and is linked instead - ACS caps the whole request at 10 MB
    // (~7.5 MB after base64), and a large run must not silently fail to send.
    private async Task<(IReadOnlyList<ReportAttachment>, string?)> BuildAttachmentAsync(
        RunEmailSummary summary, CancellationToken ct)
    {
        var json     = JsonSerializer.SerializeToUtf8Bytes(summary, s_summaryJson);
        var fileName = $"run-summary-{summary.InstanceId}.json";

        if (json.Length <= _options.MaxAttachmentBytes)
            return ([new ReportAttachment(fileName, "application/json", BinaryData.FromBytes(json))],
                    $"Full run summary attached as {fileName} ({json.Length / 1024.0:N0} KB).");

        var blobPath = ReportPath.Build(DateTimeOffset.UtcNow, "run-summary", summary.InstanceId);
        try
        {
            await _blobStore.UploadAsync(_reports, blobPath, BinaryData.FromBytes(json), overwrite: true, ct);
            return ([], $"Run summary was {json.Length / 1024.0 / 1024.0:N1} MB, over the "
                      + $"{_options.MaxAttachmentBytes / 1024 / 1024} MB attachment budget — written to "
                      + $"pipeline-reports/{blobPath} instead.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not write the oversized run summary to blob — sending without it");
            return ([], "Run summary exceeded the attachment budget and could not be written to blob.");
        }
    }

    private async Task WriteLastRunPointerAsync(RunEmailSummary summary, CancellationToken ct)
    {
        try
        {
            var c = summary.IndexReport?.Chunking;
            var pointer = new PreviousRunPointer(
                InstanceId: summary.InstanceId,
                BlobPath:   summary.BlobPath,
                FinishedAt: summary.IndexReport?.Run.FinishedAt ?? summary.RestoreReport?.FinishedAt ?? DateTimeOffset.UtcNow,
                Success:    summary.Success,
                DocsToProcess:  summary.IndexReport?.Extraction?.DocsToProcess,
                ChunksProduced: c?.ChunksProduced,
                DocsUploaded:   summary.IndexReport?.Embedding?.DocsUploaded,
                CoherentChunkRatio: c is { ChunksProduced: > 0 } ? c.CoherentChunks / (double)c.ChunksProduced : null,
                IndexDocumentCount: summary.IndexReport?.Embedding?.IndexDocumentCountSnapshot);

            await _blobStore.UploadJsonAsync(_reports, LastRunPointerPath, pointer, ct: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The email already went out; failing the activity here would misreport a
            // successful send as a failure. Losing this write only means the next run's delta
            // section falls back to "no previous run on record".
            _logger.LogWarning(ex, "Email for {InstanceId} was sent but _last-run.json could not be updated", summary.InstanceId);
        }
    }
}
