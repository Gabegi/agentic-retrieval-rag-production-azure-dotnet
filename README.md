# Agentic RAG App (cap.lz.app)

Azure-based Retrieval-Augmented Generation (RAG) app: indexes PDF documents into Azure AI Search (extraction via Azure AI Content Understanding), then answers questions over that knowledge base via an agentic query pipeline.

## Architecture Overview

```
PDF sources ──▶ Indexing.CU ──▶ Azure AI Search index
                 (extract → chunk → embed)         │
                                                  ▼
                     User question ──▶ Querying ──▶ Knowledge Base retrieval ──▶ cited answer
```

- **Indexing** (`AgenticRagApp.Indexing.CU`): extracts source documents via Azure AI Content Understanding, chunks and embeds them, and uploads to the Azure AI Search index. Runs as a Durable Functions orchestration in `AgenticRagApp.FunctionApp` (`con-func-idx-*`).
- **Querying** (`AgenticRagApp.Querying`): takes a user question, retrieves relevant chunks from the Search knowledge base, and generates a cited answer. Currently exposed through `AgenticRagApp.FunctionApp` (`POST /api/query`); `infra/app_service.tf` provisions a separate Linux App Service (`con-app-api-*`) for a future split-out query API deployment.
- **Observability** (`AgenticRagApp.Observability`): cross-cutting run reports, snapshots (for index restore), and telemetry shared by both sides.
- **Infrastructure** (`AgenticRagApp.Infrastructure`): the Azure client wiring (Search, Blob, Content Understanding, Embedding, Knowledge Base) both sides depend on.

See [Endpoints](#endpoints) for the full API surface, and the per-project READMEs under [Projects](#projects) for implementation detail.

## Endpoints

All HTTP functions use function-key auth (`AuthorizationLevel.Function`). Everything lives in `src/AgenticRagApp.FunctionApp`.

| Endpoint | Function | What it does |
| --- | --- | --- |
| `POST /api/index?force=&recreate=` | `StartIndexing` ([`PdfIndexingFunction.cs`](src/AgenticRagApp.FunctionApp/IndexingFunctions/PdfIndexingFunction.cs)) | Start an indexing run (Durable orchestration); flags below |
| `GET /api/index/status?instanceId=` | `GetIndexingStatus` ([`IndexingStatusFunction.cs`](src/AgenticRagApp.FunctionApp/IndexingFunctions/IndexingStatusFunction.cs)) | Stage-level progress of the latest (or a named) run — see [indexing-run-status.md](src/AgenticRagApp.FunctionApp/indexing-run-status.md) |
| `POST /api/index/restore` | `StartRestore` ([`IndexRestoreFunction.cs`](src/AgenticRagApp.FunctionApp/IndexingFunctions/IndexRestoreFunction.cs)) | Wipe the index, repopulate from the rolling full-corpus snapshot |
| `POST /api/index/full-recreation?confirm=<index-name>` | `FullIndexRecreation` ([`IndexAdminFunction.cs`](src/AgenticRagApp.FunctionApp/IndexingFunctions/IndexAdminFunction.cs)) | Wipe the index and rebuild it **empty** on the current schema; repopulates nothing. Destructive — `?confirm=` must exactly match the configured index name or the call is refused with `400` |
| `POST /api/setup-knowledge-base` | `SetupKnowledgeBase` ([`IndexAdminFunction.cs`](src/AgenticRagApp.FunctionApp/IndexingFunctions/IndexAdminFunction.cs)) | Ensure the knowledge source and knowledge base exist on the current index |
| `POST /api/query` (JSON body `{"question": "..."}`) | `Query` ([`QueryingFunction.cs`](src/AgenticRagApp.FunctionApp/QueryingFunctions/QueryingFunction.cs)) | Answer a question over the knowledge base with citations |
| Timer, daily 17:00 Dutch wall-clock | `ScheduledIndexing` ([`PdfIndexingFunction.cs`](src/AgenticRagApp.FunctionApp/IndexingFunctions/PdfIndexingFunction.cs)) | Full drop-and-rebuild indexing run — see [Operations](#operations) |

## Rebuilding the Whole Index in One Call

```
POST /api/index?force=true&recreate=true
```

Drops the index — plus the knowledge source and knowledge base on top of it — rebuilds it empty on
the current schema, then runs the normal extract → chunk → embed → upload pipeline over the whole
corpus, all in one Durable orchestration. This is what the daily `ScheduledIndexing` timer sends at
17:00 Dutch wall-clock time.

The two query flags are independent:

| Flag | Effect |
| --- | --- |
| `force=true` | Ignore change detection — re-extract, re-chunk and re-embed **every** source document through Content Understanding, not just new/updated ones |
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
| `POST /api/index/full-recreation?confirm=<index-name>` (`FullIndexRecreation`) | Wipes the index and rebuilds it **empty** on the current schema; repopulates nothing | You want the schema change applied now and will reindex separately |

See [Operations](#operations) for the scheduled rebuild and the full recovery procedure.

## Projects

Each title is clickable

- [`AgenticRagApp.Common`](src/AgenticRagApp.Common/README.md) — shared models used across projects
- [`AgenticRagApp.Infrastructure`](src/AgenticRagApp.Infrastructure/README.md) — Azure clients (Search, Blob, Content Understanding, Embedding, Knowledge Base) + DI wiring; see [Clients.md](src/AgenticRagApp.Infrastructure/Clients.md) for the full client/method table
- [`AgenticRagApp.Indexing.CU`](src/AgenticRagApp.Indexing.CU/README.md) — document extraction → chunking → embedding → upload pipeline
- [`AgenticRagApp.Querying`](src/AgenticRagApp.Querying/README.md) — agentic retrieval + answer generation at query time
- [`AgenticRagApp.Observability`](src/AgenticRagApp.Observability/README.md) — run reports, snapshots, telemetry
- [`AgenticRagApp.FunctionApp`](src/AgenticRagApp.FunctionApp/README.md) — Azure Functions host exposing indexing and querying endpoints
- [`Evaluations/RagApp.Evaluation.Tests`](src/Evaluations/RagApp.Evaluation.Tests/README.md) — RAG quality evaluation harness (accuracy/refusal scoring); see [Rbac.md](src/Evaluations/RagApp.Evaluation.Tests/Rbac.md) for its identity/RBAC requirements
- `UnitTests/*` — test projects for the `src/` projects above, run via `dotnet test src/AgenticRagApplication.sln`

Retired projects live in root [`archive/`](archive/) — `AgenticRagApp.Indexing.Csv` (+ its tests, CSV indexing was never wired to a Function) and `AgenticRagApp.Indexing.DI` (the old Document Intelligence extraction pipeline, replaced by Content Understanding). They are not in the solution and are not built.

## Quick Start

- Build: `dotnet build src/AgenticRagApplication.sln`
- Unit tests: `dotnet test src/AgenticRagApplication.sln`
- Run locally / configuration: see [RunningLocally.md](RunningLocally.md)
- Evals: see the [Evaluations README](src/Evaluations/RagApp.Evaluation.Tests/README.md)

> Use `src/AgenticRagApplication.sln` — the root `AgenticRetrievalChunking.sln` is stale (it still references projects that were moved to `archive/` or deleted), so a bare `dotnet build` at the repo root fails.

## Repository Structure

```
/
├── .github/workflows/          # CI (GitHub Actions, all manually triggered)
│   ├── 1-deploy-infrastructure.yml
│   ├── 2-scrape-protocols.yml
│   ├── 3-deploy-application.yml
│   ├── 4-evaluate-rag.yml
│   └── 99-destroy-infrastructure.yml
├── infra/                      # Terraform — one file per resource area (search, storage,
│                               # function_app, app_service, ai_deployments, network, ...)
├── src/                        # .NET application code (see Projects above)
│   ├── AgenticRagApp.Common/
│   ├── AgenticRagApp.Infrastructure/
│   ├── AgenticRagApp.Indexing.CU/
│   ├── AgenticRagApp.Querying/
│   ├── AgenticRagApp.Observability/
│   ├── AgenticRagApp.FunctionApp/
│   ├── Evaluations/
│   ├── UnitTests/
│   └── AgenticRagApplication.sln   # the solution to build/test
├── archive/                    # retired projects, kept for reference only — not built
├── README.md
└── RunningLocally.md
```

## Infrastructure

Terraform in [`infra/`](infra/), one file per resource area. Environments (dev/prod) share one root module, switched by `var.environment` (`development`/`production` → `dev`/`prd` in resource names); values are supplied out-of-band (`*.tfvars` is gitignored). Key resources, all private-endpoint-first:

| Resource | Name (dev) | File |
| --- | --- | --- |
| Function App (indexing + query host) | `con-func-idx-cap-dev-we-001` (Windows, EP1) | `function_app.tf` |
| Linux App Service (future query API) | `con-app-api-cap-dev-we-001` (P1v3) | `app_service.tf` |
| Azure AI Search | `con-srch-cap-dev-we-001` (S3 + semantic search) | `search.tf` |
| Storage (documents, reports, snapshots) | `constdatacapdevwe` | `storage.tf` |
| Storage (Functions runtime) | `constfunccapdevwe` | `storage.tf` |
| OpenAI model deployments | on the shared Foundry account `con-ais-cap-dev-we-001` | `ai_deployments.tf` |
| Key Vault | `con-kv-cap-dev-we-002` | `keyvault.tf` |

The Foundry account, VNet, and resource groups other than `con-cap-api-*` are platform-team owned and consumed as data sources (`data.tf`). The Search index, knowledge source, and knowledge base are created by the application (`IndexService`/`KnowledgeService`), not by Terraform.

Dev access: `dev_allowed_ips` allowlists your public IP on the Function App (main + Kudu/SCM site), the data storage account, and Search; `dev_developer_object_ids` grants Search Index Data Reader (`dev_access.tf`, `variables.tf`).

## Blob Storage Layout — Reports, Artifacts & Snapshots

See [AgenticRagApp.Observability/Reports.md](src/AgenticRagApp.Observability/Reports.md) for the full table of everything the pipelines write to blob storage, by container.

## Operations

### Scheduled Daily Rebuild

`ScheduledIndexing` (`PdfIndexingFunction`) fires once a day at **17:00 Dutch wall-clock time**
(CRON `0 0 17 * * *` with `WEBSITE_TIME_ZONE`, not UTC) and runs the index from scratch:
`RecreateIndexActivity` drops the knowledge base, the knowledge source and the index and rebuilds
them empty on the current schema, then the normal extract → chunk → embed → upload pipeline
repopulates it with `force=true`, so every source document goes through Content Understanding again.

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
     (drops and recreates the index with the current schema) followed by
     `RestoreFromSnapshotActivity` (repopulates from the rolling full-corpus snapshot,
     re-embedding only chunks missing a vector) — built for exactly this case, and cheaper
     than a full re-extraction.
  2. Check the restore report in the `pipeline-reports` container at
     `{yyyy}/{MM}/{dd}/{timestamp}Z-restore-run-{instanceId}.json` — confirm `Success: true`
     and a sane non-zero `ChunksRestored`.
  3. If the snapshot turns out empty/missing, fall back to `POST /api/index?force=true` for a
     full re-extraction through Content Understanding.
  4. Re-run the eval suite once the index is repopulated.

### Monitoring a Run

`GET /api/index/status` gives stage-level progress of a run in flight; the `INDEXING RUN FINISHED`
log line in App Insights summarizes a finished run. See
[indexing-run-status.md](src/AgenticRagApp.FunctionApp/indexing-run-status.md) for both.

## CI

CI is GitHub Actions (`.github/workflows/`), all `workflow_dispatch` (manually triggered): infra deploy, protocol scraping, app deploy, eval run, and infra destroy. The eval workflow temporarily allowlists the runner's IP on the storage account for the duration of the run.

## Branching

- **Policy**: branch fresh from `main` for each new feature; changes land via PR; delete the branch once its PR merges.
- **Naming**: `feature/<name-of-feature>` (e.g. `feature/evaluation-improvements`), kebab-case, one feature per branch.
