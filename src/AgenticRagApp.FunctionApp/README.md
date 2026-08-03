# AgenticRagApp.FunctionApp

Azure Functions host — the deployable entry point that wires the other `src/` projects together and exposes them as HTTP/durable-orchestrator endpoints.

- `IndexingFunctions/PdfIndexingFunction.cs` — orchestrates the PDF indexing pipeline (`AgenticRagApp.Indexing.Pdf`), including restore (`POST /api/index/restore`) and forced reindex (`StartIndexing?force=true`)
- `IndexingFunctions/CsvIndexingFunction.cs` — orchestrates the CSV indexing pipeline (`AgenticRagApp.Indexing.Csv`)
- `QueryingFunctions/QueryingFunction.cs` — handles `/api/query` requests via `AgenticRagApp.Querying`
- `Program.cs` — DI registration and Functions host startup

See [infra/Infrastructure.md](../../infra/Infrastructure.md#debugging-the-dev-function-app) for debug steps, and the root [ReadMe.md](../../ReadMe.md#operations) for post-deployment/recovery steps.
