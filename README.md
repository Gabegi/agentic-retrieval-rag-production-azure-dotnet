# Agentic RAG App (cap.lz.app)

Azure-based Retrieval-Augmented Generation (RAG) app: indexes PDF/CSV documents into Azure AI Search, then answers questions over that knowledge base via an agentic query pipeline.

## Architecture Overview

```
PDF/CSV sources ──▶ Indexing.Pdf / Indexing.Csv ──▶ Azure AI Search index
                     (extract → chunk → embed)         │
                                                        ▼
                     User question ──▶ Querying ──▶ Knowledge Base retrieval ──▶ cited answer
```

- **Indexing** (`AgenticRagApp.Indexing.Pdf`/`.Csv`): extracts source documents (via Document Intelligence for PDFs), chunks and embeds them, and uploads to the Azure AI Search index. Runs as a Durable Functions orchestration in `AgenticRagApp.FunctionApp` (`cor-func-idx-*`).
- **Querying** (`AgenticRagApp.Querying`): takes a user question, retrieves relevant chunks from the Search knowledge base, and generates a cited answer. Currently also exposed through `AgenticRagApp.FunctionApp` (`/api/query`); `infra/app_service.tf` provisions a separate Linux App Service (`cor-app-api-*`) for this, for a future split-out query API deployment.
- **Observability** (`AgenticRagApp.Observability`): cross-cutting run reports, snapshots (for index restore), and telemetry shared by both sides.
- **Infrastructure** (`AgenticRagApp.Infrastructure`): the Azure client wiring (Search, Blob, Document Intelligence, Embedding, Knowledge Base) both sides depend on.

See [infra/Infrastructure.md](infra/Infrastructure.md) for the underlying Azure resources, and the per-project READMEs below for implementation detail.

## Projects 

Each title is clickable
- [`AgenticRagApp.Common`](src/AgenticRagApp.Common/README.md) — shared models used across projects
- [`AgenticRagApp.Infrastructure`](src/AgenticRagApp.Infrastructure/README.md) — Azure clients (Search, Blob, Document Intelligence, Embedding, Knowledge Base) + DI wiring; see [Clients.md](src/AgenticRagApp.Infrastructure/Clients.md) for the full client/method table
- [`AgenticRagApp.Indexing.Pdf`](src/AgenticRagApp.Indexing.Pdf/README.md) — PDF extraction → chunking → embedding → upload pipeline
- [`AgenticRagApp.Indexing.Csv`](src/AgenticRagApp.Indexing.Csv/README.md) — CSV extraction → chunking → embedding → upload pipeline
- [`AgenticRagApp.Querying`](src/AgenticRagApp.Querying/README.md) — agentic retrieval + answer generation at query time
- [`AgenticRagApp.Observability`](src/AgenticRagApp.Observability/README.md) — run reports, snapshots, telemetry
- [`AgenticRagApp.FunctionApp`](src/AgenticRagApp.FunctionApp/README.md) — Azure Functions host exposing indexing and querying endpoints
- [`Evaluations/RagApp.Evaluation.Tests`](src/Evaluations/RagApp.Evaluation.Tests/README.md) — RAG quality evaluation harness (accuracy/refusal scoring); see [Rbac.md](src/Evaluations/RagApp.Evaluation.Tests/Rbac.md) for its identity/RBAC requirements
- `UnitTests/*` — one xUnit test project per `src/` project above, run with `dotnet test`

## Quick Start

See [RunningLocally.md](RunningLocally.md) for prerequisites, configuration, and build/test/eval commands.

## Branching

- **Policy**: branch fresh from `development` for each new feature; changes land via PR (no direct pushes to `development`/`production`); delete the branch once its PR merges.
- **Naming**: `feature/<name-of-feature>` (e.g. `feature/evaluation-improvements`), kebab-case, one feature per branch.

## Repository Structure

```
/
├── infra/                      # Terraform infrastructure — see infra/Infrastructure.md
│   ├── envs/
│   │   ├── dev.tfvars
│   │   └── prod.tfvars
│   └── *.tf                    # one file per resource area (search, storage, function_app, ...)
├── src/                        # .NET application code (see Projects below for what each does)
│   ├── AgenticRagApp.Common/             # shared entities 
│   ├── AgenticRagApp.Infrastructure/       # Clients
│   ├── AgenticRagApp.Indexing.Pdf/          # Complete indexing->chunking->embedding->indexing pipeline for pdf (Document Intelligence & pdfpig)
│   ├── AgenticRagApp.Indexing.Csv/          # Complete indexing->chunking->embedding->indexing pipeline for csv (not in used currently, kept in case)
│   ├── AgenticRagApp.Querying/      # Query logic to the knowledge base
│   ├── AgenticRagApp.Observability/    # Reporting, stats...
│   ├── AgenticRagApp.FunctionApp/          # Azure Functions
│   ├── Evaluations/            # RAG quality evaluation harness
│   ├── UnitTests/              # xUnit test projects, one per project above
│   └── AgenticRagApplication.sln
├── docs/                       # dated status notes
├── data/                       # sample data
├── .pipelines/                 # Azure DevOps pipelines
│   ├── pipeline.yml            # main build/deploy pipeline
│   ├── 2-infra-destroy.yml     # Terraform teardown
│   ├── 5-upload-sample-pdfs.yml
│   ├── base/                   # shared pipeline templates
│   └── templates/
└── ReadMe.md
```


## Blob Storage Layout — Reports, Artifacts & Snapshots

See [AgenticRagApp.Observability/Reports.md](src/AgenticRagApp.Observability/Reports.md) for the full table of everything the pipelines write to blob storage, by container.

## Operations

### Post-Deployment Steps

- **Run one `force=true` reindex after deploying the rolling-snapshot feature.** The snapshot only accumulates chunks touched by normal runs, so a document indexed before this feature existed (and never updated since) won't appear in it otherwise. Until that first full run, vector-cache eviction may delete still-live vectors it can't yet see in a snapshot — safe, just an avoidable re-embed later, not a correctness issue.

- **Recovery steps when the index is suspected corrupt/incomplete** (e.g. a schema change like a field's `Sortable`/`Filterable` flag can't be applied in place, since Azure AI Search only picks that up on index creation, not update):
  1. Call `StartRestore` (`POST /api/index/restore`). This runs `RecreateIndexActivity`
     (drops and recreates the index with the current schema, picking up `id`'s sortable
     flag) followed by `RestoreFromSnapshotActivity` (repopulates from the rolling
     full-corpus snapshot, re-embedding only chunks missing a vector) — built for exactly
     this "index suspected corrupt/incomplete" case, and cheaper than a full re-extraction.
  2. Check the restore report at `restore/{date}/{instanceId}.json` — confirm
     `Success: true` and a sane non-zero `ChunksRestored`.
  3. If the snapshot turns out empty/missing, fall back to `StartIndexing?force=true` for a
     full re-extraction through Document Intelligence.
  4. Re-run the eval suite once the index is repopulated.

### Debugging the Function App

See [infra/Infrastructure.md](infra/Infrastructure.md#debugging-the-dev-function-app).

## Terraform Pipeline Configuration

See [infra/Infrastructure.md](infra/Infrastructure.md) for the underlying resources these pipelines deploy.

See [Pipelines.md](src/Pipelines.md) for the pipeline configurations