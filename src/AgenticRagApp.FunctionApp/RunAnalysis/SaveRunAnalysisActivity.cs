using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using AgenticRagApp.Infrastructure.Clients.Blob;
using AgenticRagApp.Observability;
using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Functions.RunAnalysis;

public sealed record SaveRunAnalysisRequest(RunReportKind Kind, string InstanceId, DateTimeOffset StartedAt);

// Called by PdfIndexingFunction/IndexRestoreFunction immediately after
// SaveIndexReportActivity/SaveRestoreReportActivity write the run report. One run -> one
// analysis blob: the assembled summary (run report + sibling stage reports + previous-run
// pointer + eval baseline), the deterministic threshold flags, and the model's assessment,
// written to pipeline-reports alongside the reports it reads.
//
// This grew out of the run report EMAIL (docs/2608/260807/pipeline-run-email-report.md). The
// Azure Communication Services transport was removed and never replaced, which left the
// assembler, the flag evaluator and the analysis agent producing output that went nowhere - the
// no-op "sender" logged a subject line and dropped the body. The blob IS the delivery now:
// everything the email carried lands as one JSON file, in the same store operators already read
// run reports from. If a transport ever comes back, it reads this blob; nothing here re-renders.
//
// Deliberately an activity, not a blob-triggered function. An earlier version of this feature
// fired off Event Grid watching pipeline-reports/runs/ - that meant a cross-account trigger
// (the data account, not the function's own AzureWebJobsStorage), a subject filter that could
// drift, a path guard to compensate, and a daily reconciliation timer whose entire job was
// detecting that the trigger had silently stopped firing. As an activity, the orchestrator
// already knows a run finished and already knows its instance ID - none of that machinery has
// anything left to do.
//
// Every step here is best-effort against the orchestration succeeding: this activity must never
// fail a good indexing run over an analysis problem, so it catches broadly and always returns
// rather than throwing. Call it with a Durable retry policy (a few attempts) for transient
// failures inside those calls that are worth one retry - see the orchestrator's TaskOptions on
// this call.
public class SaveRunAnalysisActivity
{
    // Kept for the delta section: a pointer blob, not a folder walk. Indexing runs are
    // infrequent, so the previous run is usually days back; "look in today's folder" loses the
    // delta on the first run of every day, which is most runs.
    public const string LastRunPointerPath = RunReportAssembler.LastRunPointerPath;

    // Same shape as RunReportWriter's options, for the same reason: enum NAMES on the wire, and
    // nulls written explicitly rather than omitted - a null stage means "never ran" and the
    // schema note in docs/report-schema.md depends on readers seeing that null, not a missing
    // key they might default to zero.
    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        Converters    = { new JsonStringEnumConverter() },
    };

    private readonly IBlobStore          _blobStore;
    private readonly BlobContainerClient _reports;
    private readonly RunReportAssembler  _assembler;
    private readonly RunAnalysisAgent    _analyst;
    private readonly RunAnalysisOptions  _options;
    private readonly ILogger<SaveRunAnalysisActivity> _logger;

    // Takes BlobServiceClient and derives the container itself, rather than a plain
    // BlobContainerClient constructor parameter (which is what this had originally, and which
    // is never resolvable). The Functions Worker isolated-worker host does not activate
    // [Function] classes via serviceProvider.GetService(functionType) - it always calls
    // ActivatorUtilities.CreateInstance(scopedProvider, functionType), which resolves each
    // CONSTRUCTOR PARAMETER type directly from the container and completely ignores any
    // registration made for the class itself (e.g. services.AddSingleton<SaveRunAnalysisActivity>
    // would be dead code here - confirmed in production 2026-08-07, the exact same
    // "Unable to resolve service for type BlobContainerClient" error survived that registration
    // unchanged). No unkeyed BlobContainerClient is registered anywhere in this app - every
    // other consumer of "pipeline-reports" builds one from BlobServiceClient inside its own
    // factory closure (see Program.cs's RunReportAssembler/RunReportWriter/etc. registrations);
    // this constructor does the equivalent internally instead of asking the DI container
    // for a type nothing ever supplies. SaveRunAnalysisActivityActivationTests pins this.
    public SaveRunAnalysisActivity(
        IBlobStore blobStore,
        BlobServiceClient blobServiceClient,
        RunReportAssembler assembler,
        RunAnalysisAgent analyst,
        RunAnalysisOptions options,
        ILogger<SaveRunAnalysisActivity> logger)
    {
        _blobStore = blobStore;
        _reports   = blobServiceClient.GetBlobContainerClient("pipeline-reports");
        _assembler = assembler;
        _analyst   = analyst;
        _options   = options;
        _logger    = logger;
    }

    [Function("SaveRunAnalysisActivity")]
    public async Task Run([ActivityTrigger] SaveRunAnalysisRequest req, FunctionContext context)
    {
        var ct = context.CancellationToken;

        if (!_options.Enabled)
        {
            _logger.LogInformation("Run analysis disabled (RunAnalysis:Enabled=false) — skipping {InstanceId}", req.InstanceId);
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
                _logger.LogError("Could not read run report '{Blob}' — no run analysis written", blobName);
                Instrumentation.PipelineFailures.Add(1, new KeyValuePair<string, object?>("stage", "run_analysis_assemble"));
                return;
            }

            // Never fails the write - a model outage degrades to Assessment: null.
            summary = summary with { Assessment = await _analyst.AnalyseAsync(summary, ct) };

            // Filed under the run's StartedAt so it lands in the same date folder as the run
            // report it analyses.
            var analysisPath = ReportPath.Build(req.StartedAt, "run-analysis", req.InstanceId);
            await _blobStore.UploadJsonAsync(_reports, analysisPath, summary, s_json, ct);

            _logger.LogInformation(
                "Run analysis for {InstanceId}: {Verdict}, {Flags} flag(s) — written to pipeline-reports/{Path}",
                req.InstanceId, summary.Verdict, summary.Flags.Count, analysisPath);

            await WriteLastRunPointerAsync(summary, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The indexing run itself already succeeded by the time this activity runs - an
            // analysis problem must never surface as a failed indexing run.
            _logger.LogError(ex, "Run analysis failed unexpectedly for {InstanceId}", req.InstanceId);
            Instrumentation.PipelineFailures.Add(1, new KeyValuePair<string, object?>("stage", "run_analysis_unhandled"));
        }
    }

    private async Task WriteLastRunPointerAsync(RunSummary summary, CancellationToken ct)
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
            // The analysis blob is already written; failing the activity here would misreport
            // that as a failure. Losing this write only means the next run's delta section falls
            // back to "no previous run on record".
            _logger.LogWarning(ex, "Run analysis for {InstanceId} was written but _last-run.json could not be updated", summary.InstanceId);
        }
    }
}
