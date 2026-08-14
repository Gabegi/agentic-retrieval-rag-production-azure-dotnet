# AgenticRagApp.FunctionApp

Azure Functions host — the deployable entry point that wires the other `src/` projects together and exposes them as HTTP/durable-orchestrator endpoints.

- `IndexingFunctions/PdfIndexingFunction.cs` — orchestrates the PDF indexing pipeline (`AgenticRagApp.Indexing.Pdf`), including forced reindex (`StartIndexing?force=true`)
- `IndexingFunctions/IndexingStatusFunction.cs` — `GET /api/index/status`, stage-level progress of the latest (or a named) run
- `IndexingFunctions/IndexRestoreFunction.cs` — restore (`POST /api/index/restore`): wipes the index and repopulates it from the rolling snapshot
- `IndexingFunctions/IndexAdminFunction.cs` — operator endpoints: `POST /api/index/full-recreation` (destructive, empty rebuild) and `POST /api/setup-knowledge-base`
- `QueryingFunctions/QueryingFunction.cs` — handles `/api/query` requests via `AgenticRagApp.Querying`
- `Program.cs` — DI registration and Functions host startup

> `AgenticRagApp.Indexing.Csv` has a complete pipeline (see its [README](../AgenticRagApp.Indexing.Csv/README.md)) but is not yet wired to a Function here — there is no `CsvIndexingFunction`.

## See also

- [indexing-run-status.md](indexing-run-status.md) — checking what an indexing run is doing (`GET /api/index/status`) and what it did once finished
- [infra/Infrastructure.md](../../infra/Infrastructure.md#debugging-the-dev-function-app) — debugging the dev Function App
- root [ReadMe.md](../../ReadMe.md#operations) — post-deployment/recovery steps
