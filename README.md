# Agentic RAG App (cap.lz.app)

Azure-based Retrieval-Augmented Generation (RAG) app: indexes PDF/CSV documents into Azure AI Search, then answers questions over that knowledge base via an agentic query pipeline.

## Rebuilding the Whole Index in One Call

```
POST /api/index?force=true&recreate=true
```

`StartIndexing` ([`PdfIndexingFunction.cs`](src/AgenticRagApp.FunctionApp/IndexingFunctions/PdfIndexingFunction.cs),
function-key auth). Drops the index — plus the knowledge source and knowledge base on top of it —
rebuilds it empty on the current schema, then runs the normal extract → chunk → embed → upload
pipeline over the whole corpus, all in one Durable orchestration. This is what the daily
`ScheduledIndexing` timer sends at 17:00 Dutch wall-clock time.

The two query flags are independent:

| Flag | Effect |
| --- | --- |
| `force=true` | Ignore change detection — re-extract, re-chunk and re-embed **every** source document through Document Intelligence, not just new/updated ones |
| `recreate=true` | Run `RecreateIndexActivity` first: drop the index + knowledge source/base and rebuild them empty on the current schema, then continue into the pipeline |

- **The index answers nothing until the run finishes** — it is empty from the recreate until the
  upload stage lands. Queries in that window return no results.
- `recreate=true` alone wipes the index but then only refills what change detection considers
  new — rarely what you want after a wipe. Pair it with `force=true`.
- `force=true` alone reprocesses everything into the **existing** index without dropping it, so
  it won't pick up a schema change.
- No `?confirm=` guard here, unlike `FullIndexRecreation` — this path repopulates in the same
  run rather than leaving the index empty.

Other one-click paths, for when this isn't the one you want:

| Endpoint | What it does | Use when |
| --- | --- | --- |
| `POST /api/index/restore` (`StartRestore`) | Wipes the index, repopulates from the rolling full-corpus snapshot | Index suspected corrupt/incomplete — but the snapshot is in the *previous* schema shape, so useless after a field rename |
| `POST /api/index/full-recreation?confirm=<index-name>` (`FullIndexRecreation`) | Wipes the index and rebuilds it **empty** on the current schema; repopulates nothing | You want the schema change applied now and will reindex separately. Destructive and irreversible — `?confirm=` must exactly match the configured index name or the call is refused with `400` |

See [Operations](#operations) for the scheduled rebuild and the full recovery procedure.

## Architecture Overview

```
PDF/CSV sources ──▶ Indexing.Pdf / Indexing.Csv ──▶ Azure AI Search index
                     (extract → chunk → embed)         │
                                                        ▼
                     User question ──▶ Querying ──▶ Knowledge Base retrieval ──▶ cited answer
```

- **Indexing** (`AgenticRagApp.Indexing.Pdf`/`.Csv`): extracts source documents (via Document Intelligence for PDFs), chunks and embeds them, and uploads to the Azure AI Search index. Runs as a Durable Functions orchestration in `AgenticRagApp.FunctionApp` (`con-func-idx-*`).
- **Querying** (`AgenticRagApp.Querying`): takes a user question, retrieves relevant chunks from the Search knowledge base, and generates a cited answer. Currently also exposed through `AgenticRagApp.FunctionApp` (`/api/query`); `infra/app_service.tf` provisions a separate Linux App Service (`con-app-api-*`) for this, for a future split-out query API deployment.
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

### Scheduled Daily Rebuild

`ScheduledIndexing` (`PdfIndexingFunction`) fires once a day at **17:00 Dutch wall-clock time**
(`WEBSITE_TIME_ZONE`, not UTC) and runs the index from scratch: `RecreateIndexActivity` drops
the knowledge base, the knowledge source and the index and rebuilds them empty on the current
schema, then the normal extract → chunk → embed → upload pipeline repopulates it with
`force=true`, so every source document goes through Document Intelligence again.

- **The index answers nothing between 17:00 and the run finishing** — it is empty from the
  recreate until the upload stage lands. Queries during that window return no results.
- A fixed instance ID (`PdfIndexing`) keeps it single-flight: if a run is still going at the
  next tick, that tick is skipped rather than overlapping.
- Same thing on demand: `POST /api/index?force=true&recreate=true`. Without `recreate=true`
  the run indexes into the existing index as before.
- Cheaper steady-state once the corpus is stable: drop both flags on the timer (diff-only) and
  keep the recreate for schema changes. See the TODO on `RunScheduled`.

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