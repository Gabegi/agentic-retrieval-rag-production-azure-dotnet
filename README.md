# agentic-retrieval-chunking

A production-shaped Retrieval-Augmented Generation (RAG) pipeline in .NET and Terraform, built around Azure AI Search's **agentic retrieval** (knowledge base) feature and Azure AI Foundry. The knowledge base plans its own search queries and synthesizes the final answer, so there's no separate hand-rolled retrieval + chat step in production.

It indexes a mixed document corpus (CSV records and PDFs), with a validation/quality gate between extraction and indexing, and an automated evaluation harness that scores answer quality on every run.

## Repository layout

- **`src/AgenticRagApp.FunctionApp`** — Azure Functions app, the deployable entry point:
  - `IndexingFunctions/CsvIndexingFunction.cs` and `PdfIndexingFunction.cs` — durable orchestrations that extract, chunk, embed, and write documents to Azure AI Search.
  - `QueryingFunctions/QueryingFunction.cs` (`POST /api/query`) — answers questions via `AgenticRagQueryService`.

- **`src/AgenticRagApp.Indexing.Csv`** / **`src/AgenticRagApp.Indexing.Pdf`** — extraction, cleaning, and chunking pipelines for each document type. The PDF pipeline additionally validates extraction quality (`PdfPipelineValidator`) before anything is written to the index — a bad run fails closed instead of silently deleting good documents from a passed run.

- **`src/AgenticRagApp.Querying`** — `AgenticRagQueryService` orchestrates a knowledge base retrieval call and delegates the rest to focused collaborators:
  - `KnowledgeBaseReferenceMapper` — parses knowledge base references into retrieved chunks.
  - `ChunkNeighborExpander` — fetches neighboring pages via a raw Search side-channel when an answer likely continues onto an adjacent page.
  - `KnowledgeBaseActivitySummary` — sums token usage from the knowledge base's per-step activity records.

- **`src/AgenticRagApp.Infrastructure`** — Azure clients (Search, Blob, Document Intelligence) and configuration.

- **`src/AgenticRagApp.Observability`** — structured diagnostics/reporting shared across the indexing pipelines.

- **`src/AgenticRagApp.Common`** — shared models used across projects.

- **`src/Evaluations/RagApp.Evaluation.Tests`** — MSTest eval harness. Runs every scenario in `testdata/golden-questions.json` through `AgenticRagQueryService` and scores it with Groundedness/Relevance/Coherence/Equivalence/Retrieval/F1 evaluators (`Microsoft.Extensions.AI.Evaluation`). Only Groundedness gates the build; the rest are tracked as trends.

- **`src/scraper`** — scrapes source documents ahead of indexing.

- **`src/UnitTests`** — unit tests for the indexing, querying, and function-app layers.

- **`infra/`** — Terraform for the full stack: resource group, Azure AI Search, Azure OpenAI, Document Intelligence, Storage, the Function App, and monitoring.

## Running the eval

```
dotnet test src/Evaluations/RagApp.Evaluation.Tests/RagApp.Evaluation.Tests.csproj -c Release --filter "TestCategory=golden"
```

Requires `SEARCH_ENDPOINT`, `OPENAI_ENDPOINT`, `OPENAI_GPT_DEPLOYMENT`, etc. as environment variables (see `src/Evaluations/RagApp.Evaluation.Tests/.env.example` for the full list) and an Azure identity with Search/OpenAI access.

## CI

- `1-deploy-infrastructure.yml` — `terraform apply`.
- `2-scrape-protocols.yml` — runs the scraper.
- `3-deploy-application.yml` — deploys the Function App.
- `4-evaluate-rag.yml` — runs the golden eval suite against live infra.

## Branching

Branch fresh from `main` for each feature; changes land via PR (no direct pushes to `main`); delete the branch once its PR merges.

## Operations

### Post-deployment

- **Run one `force=true` reindex after deploying the rolling-snapshot feature.** The snapshot only accumulates chunks touched by normal runs, so a document indexed before this feature existed (and never updated since) won't appear in it otherwise. Until that first full run, vector-cache eviction may delete still-live vectors it can't yet see in a snapshot — safe, just an avoidable re-embed later, not a correctness issue.

### Recovery when the index is suspected corrupt/incomplete

(e.g. a schema change like a field's `Sortable`/`Filterable` flag can't be applied in place, since Azure AI Search only picks that up on index creation, not update)

1. Call `StartRestore` (`POST /api/index/restore`). This runs `RecreateIndexActivity` (drops and recreates the index with the current schema, picking up `id`'s sortable flag) followed by `RestoreFromSnapshotActivity` (repopulates from the rolling full-corpus snapshot, re-embedding only chunks missing a vector) — built for exactly this "index suspected corrupt/incomplete" case, and cheaper than a full re-extraction.
2. Check the restore report at `restore/{date}/{instanceId}.json` — confirm `Success: true` and a sane non-zero `ChunksRestored`.
3. If the snapshot turns out empty/missing, fall back to `StartIndexing?force=true` for a full re-extraction through Document Intelligence.
4. Re-run the eval suite once the index is repopulated.

## Blob storage layout — reports, artifacts & snapshots

See [`src/AgenticRagApp.Observability/Reports.md`](src/AgenticRagApp.Observability/Reports.md) for the full table of everything the pipelines write to blob storage, by container.
