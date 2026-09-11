# Agentic RAG App (cap.lz.app)

Azure-hosted Retrieval-Augmented Generation app. It indexes PDFs from blob storage into an Azure
AI Search index (extract → chunk → embed → upload) and answers questions over that index through
an Azure AI Search **knowledge base** (agentic retrieval + answer synthesis), returning a cited
answer.

## How it works

```
                 ┌───────────────────────── source documents ──────────────────────────┐
                 │  blob container "documents"        (hand-uploaded PDFs)              │
                 │  blob container "zenya-documents"  (ZenyaSync tool → pdf/ and docs/; │
                 │                                     the indexer is not yet pointed   │
                 │                                     at it — see Zenya document sync) │
                 └────────────────────────────────┬────────────────────────────────────┘
                                                  ▼
   Durable orchestration "IndexingOrchestrator"  (AgenticRagApp.FunctionApp, con-func-idx-*)
   ┌────────────────┐   ┌────────────────┐   ┌─────────────────────────┐   ┌──────────────────┐
   │ ExtractActivity│ → │ ChunkActivity  │ → │ EmbedAndUploadActivity  │ → │ SaveIndexReport  │
   │ diff vs index, │   │ identity,      │   │ vector cache, batches   │   │ + SaveRunAnalysis│
   │ Content        │   │ route, cut,    │   │ of 100, upsert, stale-  │   │ (blob reports)   │
   │ Understanding  │   │ metadata       │   │ chunk cleanup, snapshot │   │                  │
   └────────────────┘   └────────────────┘   └─────────────────────────┘   └──────────────────┘
                                                  │
                                                  ▼
                             Azure AI Search index  ←  knowledge source  ←  knowledge base
                                                  ▲
                                                  │  agentic retrieval + answer synthesis
   POST /api/query { "question": "…" }  →  AgenticRagQueryService  →  guards  →  cited answer
```

- **Extraction** runs on **Azure AI Content Understanding** (`prebuilt-documentSearch` analyzer,
  on a shared Foundry AI Services account). It returns markdown plus typed structure (headings,
  tables, figures with descriptions, page spans); the app maps the typed response and never
  parses the markdown heuristically. Document Intelligence and the PdfPig preflight were removed
  in August 2026 — the retired pipeline is in [`archive/`](archive/).
- **Chunking** resolves each document's identity (family, domain tag, confusable titles), then
  routes it to one of two strategies — `DeclaredBoundaryStrategy` (honour the document's own
  headings) or `RecursiveStrategy` (no trustworthy structure; cut the whole document with the
  block cascade) — against a 512-token ceiling.
- **Embedding** uses `text-embedding-3-large` (3072 dims) with a content-hash vector cache, so
  unchanged chunks are never re-embedded. **Upload** upserts to the index, deletes chunks of
  stale documents, and updates the rolling full-corpus snapshot used for restores.
- **Querying** does not call a chat model itself: the Azure AI Search knowledge base decomposes
  the question, searches, and synthesizes the answer. The app adds neighbouring-page context,
  runs PII and prompt-injection guards, maps references to citations, and writes a per-query
  report. See [AgenticRagApp.Querying/README.md](src/AgenticRagApp.Querying/README.md) for the
  guards' current enforcement mode (log-only).
- **Observability**: every run writes an `index-run` report, per-stage artifacts, and a
  `run-analysis` blob (deterministic flags + a model assessment) to the `pipeline-reports`
  container. Telemetry goes to Application Insights via OpenTelemetry.

## Projects

All under `src/`, one solution: `src/AgenticRagApplication.sln`. .NET 10, warnings are errors in
production projects, NuGet restore runs in locked mode (`packages.lock.json` per project).

| Project | What it is | README |
|---|---|---|
| `AgenticRagApp.Common` | Shared contracts (chunk, extraction, issue, reporting base types) — no logic | [README](src/AgenticRagApp.Common/README.md) |
| `AgenticRagApp.Infrastructure` | Every Azure SDK client behind a thin wrapper, `IndexerConfig`, DI wiring, and the Zenya API client + sync | [README](src/AgenticRagApp.Infrastructure/README.md) · [Clients.md](src/AgenticRagApp.Infrastructure/Clients.md) |
| `AgenticRagApp.Indexing.CU` | The indexing pipeline: extraction (Content Understanding), document identity, chunking, embedding, upload, restore | [README](src/AgenticRagApp.Indexing.CU/README.md) |
| `AgenticRagApp.Querying` | Query-time pipeline: knowledge-base retrieval, neighbour expansion, guards, citations | [README](src/AgenticRagApp.Querying/README.md) · [AcceptatieCriteria.md](src/AgenticRagApp.Querying/AcceptatieCriteria.md) |
| `AgenticRagApp.Observability` | Run reports, stage artifacts, snapshots, run analysis, OpenTelemetry instrumentation | [README](src/AgenticRagApp.Observability/README.md) · [Reports.md](src/AgenticRagApp.Observability/Reports.md) |
| `AgenticRagApp.FunctionApp` | The deployable Azure Functions host (Durable orchestrations + HTTP endpoints) | [README](src/AgenticRagApp.FunctionApp/README.md) · [indexing-run-status.md](src/AgenticRagApp.FunctionApp/indexing-run-status.md) |
| `AgenticRagApp.Tools.ZenyaSync` | Console host that runs the Zenya → blob document sync | see [Zenya document sync](#zenya-document-sync) |
| `Evaluations/RagApp.Evaluation.Tests` | Golden-questions eval harness against a live environment (MSTest; not part of the unit-test run) | [README](src/Evaluations/RagApp.Evaluation.Tests/README.md) · [Rbac.md](src/Evaluations/RagApp.Evaluation.Tests/Rbac.md) |
| `UnitTests/*.Tests` | One MSTest project per production project (six projects) | — |

Retired projects live in root [`archive/`](archive/) — `AgenticRagApp.Indexing.Csv` (+ its tests,
CSV indexing was never wired to a Function) and `AgenticRagApp.Indexing.DI` (the old Document
Intelligence extraction pipeline, replaced by Content Understanding). They are not in the
solution and are not built.

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
| `force=true` | Ignore change detection — re-extract (billed Content Understanding pages), re-chunk and re-embed **every** source document, not just new/updated ones. The vector cache still avoids re-embedding unchanged chunks |
| `recreate=true` | Run `RecreateIndexActivity` first: drop the index + knowledge source/base and rebuild them empty on the current schema, then continue into the pipeline |

- **The index answers nothing until the run finishes** — it is empty from the recreate until the
  upload stage lands. Queries in that window return no results.
- `recreate=true` alone wipes the index but then only refills what change detection considers
  new — rarely what you want after a wipe. Pair it with `force=true`.
- `force=true` alone reprocesses everything into the **existing** index without dropping it, so
  it won't pick up a schema change (`EnsureIndexAsync` is get-or-create and never updates a live
  index).
- No `?confirm=` guard here, unlike `FullIndexRecreation` — this path repopulates in the same
  run rather than leaving the index empty.

Other one-click paths, for when this isn't the one you want:

| Endpoint | What it does | Use when |
| --- | --- | --- |
| `POST /api/index/restore` (`StartRestore`) | Wipes the index, repopulates from the rolling full-corpus snapshot | Index suspected corrupt/incomplete — but the snapshot is in the *previous* schema shape, so useless after a field rename |
| `POST /api/index/full-recreation?confirm=<index-name>` (`FullIndexRecreation`) | Wipes the index and rebuilds it **empty** on the current schema; repopulates nothing | You want the schema change applied now and will reindex separately |

See [Operations](#operations) for the scheduled rebuild and the full recovery procedure.

## Zenya document sync

Zenya (iProva) is a document management system and an intended source of the corpus.
`src/AgenticRagApp.Tools.ZenyaSync` is a console host that mirrors Zenya's published document
listing into the `zenya-documents` container: `pdf/{document_id}.pdf`,
`docs/{document_id}.{ext}`, with `zenya_*` blob metadata (`zenya_document_id`, `zenya_version`,
`zenya_status`, `zenya_title`, `zenya_mime_type`, `zenya_last_modified`, `zenya_synced_at`, …).
`zenya_version` is the change signal; `zenya_document_id` is what the removal pass keys on.

Authentication uses a client assertion from a user-assigned managed identity — there is no secret
in the integration; Zenya trusts the identity by tenant and client id. Configuration is
environment variables (`ZENYA_BASE_URL`, `STORAGE_ACCOUNT_URL`, `STORAGE_CONTAINER`, …); see
[ZenyaOptions.cs](src/AgenticRagApp.Infrastructure/Clients/Zenya/ZenyaOptions.cs) and
[ZenyaSyncOptions.cs](src/AgenticRagApp.Infrastructure/Clients/Zenya/Sync/ZenyaSyncOptions.cs).

Dry run is the default. Exit codes: `0` every listed document handled, `1` the run completed but
one or more documents failed, `2` not authenticated (Zenya answered `/users/me` as Anonymous —
nothing was synced).

The indexer still reads the `documents` container; repointing it at `zenya-documents/pdf/` and
reviving the `zenya_*` index fields is deferred.

## Quick Start

- Build: `dotnet build src/AgenticRagApplication.sln`
- Unit tests: `dotnet test src/AgenticRagApplication.sln`
- Run locally / configuration: see [RunningLocally.md](RunningLocally.md)
- Evals: see the [Evaluations README](src/Evaluations/RagApp.Evaluation.Tests/README.md)

> Use `src/AgenticRagApplication.sln` — the root `AgenticRetrievalChunking.sln` is stale (it still references projects that were moved to `archive/` or deleted), so a bare `dotnet build` at the repo root fails.

## Configuration

Configuration is environment variables / Function App settings, validated at startup with a named
list of missing keys. The full table (required, indexing-only, optional with defaults) is in
[RunningLocally.md](RunningLocally.md#configuration); deployed values come from
`infra/function_app.tf`.

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
│   ├── Directory.Build.props     # net10.0, nullable, locked restore, warnings-as-errors
│   ├── Directory.Packages.props  # central package versions
│   ├── AgenticRagApp.Common/
│   ├── AgenticRagApp.Infrastructure/
│   ├── AgenticRagApp.Indexing.CU/
│   ├── AgenticRagApp.Querying/
│   ├── AgenticRagApp.Observability/
│   ├── AgenticRagApp.FunctionApp/
│   ├── AgenticRagApp.Tools.ZenyaSync/
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

### Apply a schema change

Index fields are only created at index creation. Either `POST /api/index?force=true&recreate=true`
(repopulates in the same run) or `POST /api/index/full-recreation?confirm=<index-name>` followed
by a separate `POST /api/index?force=true` (the index stays empty in between). The run-analysis
flags include `IndexSchemaComparer` drift, so a live index that no longer matches the code's
schema shows up in the next run's analysis.

### Post-Deployment Steps

- **Run one `force=true` reindex after deploying the rolling-snapshot feature.** The snapshot only accumulates chunks touched by normal runs, so a document indexed before this feature existed (and never updated since) won't appear in it otherwise. Until that first full run, vector-cache eviction may delete still-live vectors it can't yet see in a snapshot — safe, just an avoidable re-embed later, not a correctness issue.

- If the deployment changed extraction or chunking, run `POST /api/index?force=true` so every
  chunk is regenerated by the new code (a diff-only run touches only new/updated documents).

- **Recovery steps when the index is suspected corrupt/incomplete** (e.g. a schema change like a field's `Sortable`/`Filterable` flag can't be applied in place, since Azure AI Search only picks that up on index creation, not update):
  1. Call `StartRestore` (`POST /api/index/restore`). This runs `RecreateIndexActivity`
     (drops and recreates the index with the current schema) followed by
     `RestoreFromSnapshotActivity` (repopulates from the rolling full-corpus snapshot,
     re-embedding only chunks missing a vector) — built for exactly this case, and cheaper
     than a full re-extraction.
  2. Check the restore report in the `pipeline-reports` container at
     `{yyyy}/{MM}/{dd}/{timestamp}Z-restore-run-{instanceId}.json` — confirm `Success: true`
     and a sane non-zero `ChunksRestored`.
  3. If the snapshot turns out empty/missing, or predates a schema change (its rows are in the
     *old* shape), fall back to `POST /api/index?force=true&recreate=true` for a full
     re-extraction through Content Understanding.
  4. Re-run the eval suite once the index is repopulated.

### Monitoring a Run

`GET /api/index/status` gives stage-level progress of a run in flight; the `INDEXING RUN FINISHED`
log line in App Insights summarizes a finished run. See
[indexing-run-status.md](src/AgenticRagApp.FunctionApp/indexing-run-status.md) for both.

## Known gaps

- **Guards do not block.** `IndexerConfig.GuardsLogOnly` is `true` and is **not** read from any
  app setting (it is absent from the `IndexerConfig` initializer in
  `AgenticRagApp.Infrastructure/Clients/ServiceCollectionExtensions.cs`), so the prompt-injection
  and PII acceptance criteria are logged, not enforced, in every environment. Flipping it is a
  code change today.
- `infra/app_service.tf` provisions a Linux App Service (`con-app-api-*`) for a split-out query
  API, but nothing deploys to it; `/api/query` is served by the Function App.

## CI

CI is GitHub Actions (`.github/workflows/`), all `workflow_dispatch` (manually triggered): infra deploy, protocol scraping, app deploy, eval run, and infra destroy. The eval workflow temporarily allowlists the runner's IP on the storage account for the duration of the run.

## Branching

- **Policy**: branch fresh from `main` for each new feature; changes land via PR; delete the branch once its PR merges.
- **Naming**: `feature/<name-of-feature>` (e.g. `feature/evaluation-improvements`), kebab-case, one feature per branch.
