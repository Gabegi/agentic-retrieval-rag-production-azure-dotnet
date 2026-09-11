# AgenticRagApp.FunctionApp

The deployable Azure Functions host (`dotnet-isolated`, Durable Functions, task hub
`IndexingHub`). It wires the other `src/` projects together in `Program.cs` and exposes them as
HTTP endpoints, a timer, and Durable orchestrations. Deployed as `con-func-idx-cap-<env>-we-001`.

```
Program.cs                                   OpenTelemetry (logs/traces/metrics → App Insights), AddAgenticRagAppInfrastructure,
                                             report/artifact/snapshot writers, AddQuerying, AddIndexing, run-analysis services
IndexingFunctions/
  PdfIndexingFunction.cs                     StartIndexing (POST /api/index), ScheduledIndexing (timer), IndexingOrchestrator,
                                             ExtractActivity, ChunkActivity, EmbedAndUploadActivity, SaveIndexReportActivity
  IndexRestoreFunction.cs                    StartRestore (POST /api/index/restore), RestoreOrchestrator, RecreateIndexActivity,
                                             RestoreFromSnapshotActivity, SaveRestoreReportActivity
  IndexingStatusFunction.cs                  GetIndexingStatus (GET /api/index/status)
  IndexAdminFunction.cs                      FullIndexRecreation (POST /api/index/full-recreation?confirm=), SetupKnowledgeBase (POST /api/setup-knowledge-base)
QueryingFunctions/QueryingFunction.cs        Query (POST /api/query) → IRagQueryService, writes a QueryRunReport
RunAnalysis/SaveRunAnalysisActivity.cs       after either report activity: assembles + writes the run-analysis blob; never fails the run
Models/
  IndexingProgress.cs                        the Durable custom-status payload (stage + counts) that /api/index/status reads
  PdfActivityRequests.cs                     the activity inputs (blob names, instance id, started-at)
host.json                                    functionTimeout -1, activityFunctionTimeout 01:00:00, OpenTelemetry telemetry mode
```

The endpoint table with flags and operating procedures is in the
[root README](../../ReadMe.md#endpoints).

## How the orchestration passes data

Extracted documents, chunks, stale document IDs and family moves are written to the
`indexing-pipeline` container (`{date}/{instanceId}/*.json`) and only the blob names travel
through Durable state — Durable Table Storage rows are capped at 64 KB. Stage-boundary progress
is published with `SetCustomStatus` (`IndexingProgress`); counts are `null` until the stage
that measures them finishes.

Extraction is **one** activity that fans out internally (bounded parallelism), so a host recycle
mid-extraction reruns the whole activity and re-bills the pages already analysed. Accepted for the
current corpus size; per-document fan-out is designed but deferred (`docs/2607/260729/extraction-fanout-proposal.md`, D018).

## Tests

`src/UnitTests/AgenticRagApp.FunctionApp.Tests` (MSTest, 8 files).

## See also

- [indexing-run-status.md](indexing-run-status.md) — watching a run (`GET /api/index/status`) and reading the `INDEXING RUN FINISHED` log line
- [infra/Infrastructure.md](../../infra/Infrastructure.md#debugging-the-dev-function-app) — Kudu, log tail, IP allow-listing
- [Reports.md](../AgenticRagApp.Observability/Reports.md) — everything a run writes
