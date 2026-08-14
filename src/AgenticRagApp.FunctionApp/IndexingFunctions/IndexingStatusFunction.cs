using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask.Client;

namespace AgenticRagApp.Functions;

// Read-only view over a run's Durable custom status - see PdfIndexingFunction for the
// pipeline that writes it and IndexRestoreFunction for the restore run that reuses the
// same status shape.
public class IndexingStatusFunction
{
    // How far back GetIndexingStatus looks when no instanceId is given, and how many
    // instances it will page through before giving up. Durable's query API can't filter by
    // orchestration name, so the name filter is client-side and the scan is capped rather
    // than unbounded - a status check must stay cheap even once the task hub has a long
    // history behind it.
    private const int LatestRunLookbackDays = 14;
    private const int MaxInstancesScanned   = 500;

    // Progress for a run in flight, without needing the instance ID or the Durable
    // statusQueryGetUri handed back by StartIndexing - "is it still going, which stage, how
    // long has it been there". Defaults to the most recent indexing run; pass ?instanceId= to
    // pin a specific one (including a restore run, whose stage vocabulary differs but whose
    // status payload is the same shape).
    //
    // Resolution is stage-level only: extraction is a single long activity, so a run sits on
    // "extracting" for however long extraction takes. See IndexingProgress for why finer
    // progress needs more than custom status.
    [Function("GetIndexingStatus")]
    public async Task<HttpResponseData> GetIndexingStatus(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "index/status")] HttpRequestData req,
        [DurableClient] DurableTaskClient client)
    {
        var ct         = req.FunctionContext.CancellationToken;
        var instanceId = req.Query["instanceId"];

        // getInputsAndOutputs/FetchInputsAndOutputs is what makes the custom status payload
        // come back at all - without it the stage would always read as unknown.
        var metadata = string.IsNullOrWhiteSpace(instanceId)
            ? await FindLatestIndexingRunAsync(client, ct)
            : await client.GetInstanceAsync(instanceId, getInputsAndOutputs: true, ct);

        if (metadata is null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteAsJsonAsync(new
            {
                message = string.IsNullOrWhiteSpace(instanceId)
                    ? $"No indexing run found in the last {LatestRunLookbackDays} days."
                    : $"No orchestration found with instance ID '{instanceId}'.",
            });
            return notFound;
        }

        var progress = ReadProgress(metadata);
        var running  = metadata.RuntimeStatus is OrchestrationRuntimeStatus.Running
                                              or OrchestrationRuntimeStatus.Pending
                                              or OrchestrationRuntimeStatus.Suspended;

        // LastUpdatedAt is the finish time only once the run is terminal; while it's still
        // going it's just the last checkpoint, so elapsed has to run against the wall clock.
        DateTimeOffset? finishedAt = running ? null : metadata.LastUpdatedAt;
        var elapsed = (finishedAt ?? DateTimeOffset.UtcNow) - metadata.CreatedAt;

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            instanceId     = metadata.InstanceId,
            orchestration  = metadata.Name,
            runtimeStatus  = metadata.RuntimeStatus.ToString(),
            // "starting" covers the window between scheduling and the orchestrator's first
            // SetCustomStatus, where there is genuinely no stage yet.
            stage          = progress?.Stage ?? "starting",
            startedAt      = metadata.CreatedAt,
            finishedAt,
            elapsed        = elapsed.ToString(@"hh\:mm\:ss"),
            // Null until the stage that measures them completed - "not measured yet", not zero,
            // the same distinction PdfIndexRunReport draws.
            docsExtracted  = progress?.DocsExtracted,
            chunksProduced = progress?.ChunksProduced,
            docsUploaded   = progress?.DocsUploaded,
            error          = metadata.FailureDetails?.ErrorMessage,
        });
        return response;
    }

    private static async Task<OrchestrationMetadata?> FindLatestIndexingRunAsync(
        DurableTaskClient client, CancellationToken ct)
    {
        var query = new OrchestrationQuery
        {
            CreatedFrom           = DateTimeOffset.UtcNow.AddDays(-LatestRunLookbackDays),
            FetchInputsAndOutputs = true,
        };

        OrchestrationMetadata? latest = null;
        var scanned = 0;

        await foreach (var instance in client.GetAllInstancesAsync(query).WithCancellation(ct))
        {
            if (++scanned > MaxInstancesScanned) break;
            if (instance.Name != "IndexingOrchestrator") continue;
            // Ordering isn't guaranteed by the query API, so pick the newest explicitly
            // rather than trusting the first result.
            if (latest is null || instance.CreatedAt > latest.CreatedAt) latest = instance;
        }

        return latest;
    }

    // Custom status is absent before the orchestrator's first SetCustomStatus, and could be
    // an older shape for a run still in flight across a deployment - neither is worth failing
    // a status check over, so both degrade to "stage unknown" rather than throwing.
    private static IndexingProgress? ReadProgress(OrchestrationMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata.SerializedCustomStatus)) return null;

        try
        {
            return metadata.ReadCustomStatusAs<IndexingProgress>();
        }
        catch
        {
            return null;
        }
    }
}
