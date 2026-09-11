# Running Locally

## Prerequisites

- **.NET 10 SDK** (`Directory.Build.props` targets `net10.0`; the pipeline installs `10.0.x`).
- **Azure CLI**, logged in (`az login`) as an identity that can reach the dev resources. All
  Azure access is `DefaultAzureCredential` — no keys anywhere. For dev, your account must be in
  `dev_developer_object_ids` in `infra/envs/dev.tfvars` (Search reader) and your IP in
  `dev_allowed_ips` or temporarily allow-listed — see
  [infra/Infrastructure.md](infra/Infrastructure.md#debugging-the-dev-function-app).
- **Azure Functions Core Tools v4** only if you want to run the Functions host itself
  (`func start` in `src/AgenticRagApp.FunctionApp`). Unit tests and the eval suite do not need it.

Every resource is private-endpoint-only; from outside the platform network you need the IP
allow-listing above for Storage, Search and the Function App.

## Build & test

```
dotnet restore src/AgenticRagApplication.sln --locked-mode
dotnet build   src/AgenticRagApplication.sln -c Release --no-restore
```

- There is no solution or project at the repository root — always name `src/AgenticRagApplication.sln`.
- `--locked-mode` restores the exact graph in each project's `packages.lock.json` and fails on
  drift (`NU1004`), which is what CI does. After changing a version in
  `src/Directory.Packages.props`, regenerate the lock files with
  `dotnet restore src/AgenticRagApplication.sln --force-evaluate` and commit them.
- Production projects build with warnings as errors; test projects do not.

Unit tests are MSTest, one project per production project. Run them per project — do **not**
`dotnet test` the whole solution, because `RagApp.Evaluation.Tests` is in it and calls live
Azure:

```
for p in Common Infrastructure Observability Querying Indexing.CU FunctionApp; do
  dotnet test src/UnitTests/AgenticRagApp.$p.Tests -c Release --no-build
done
```

The eval suite is run separately — see
[Evaluations README](src/Evaluations/RagApp.Evaluation.Tests/README.md) (`.env.example` →
`.env`). Re-run it after any restore or reindex.

## Configuration

The Function App reads plain environment variables (in Azure: app settings from
`infra/function_app.tf`; locally: `local.settings.json` → `Values`, or exported variables).
`AddAgenticRagAppInfrastructure` (`AgenticRagApp.Infrastructure/Clients/ServiceCollectionExtensions.cs`)
assembles `IndexerConfig` from them and fails fast at startup with a named list of what is
missing. Keys are `SCREAMING_SNAKE_CASE`; nested keys use `:` in configuration and `__` as an
environment variable.

### Required by the host

| Setting | Purpose |
|---|---|
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | OpenTelemetry exporters (logs, traces, metrics) |
| `AzureWebJobsStorage__accountName` | Functions/Durable storage account; also where the `indexing-pipeline` temp container lives (`pipeline-temp`) |
| `FUNCTIONS_WORKER_RUNTIME` | `dotnet-isolated` |

### Required by `IndexerConfig` (both indexing and querying)

| Setting | Purpose |
|---|---|
| `SEARCH_ENDPOINT` | Azure AI Search service |
| `SEARCH_INDEX_NAME` / `KNOWLEDGE_SOURCE_NAME` / `KNOWLEDGE_BASE_NAME` | Index and the knowledge source/base built on it |
| `STORAGE_ACCOUNT_URL` | Data storage account (`documents`, `pipeline-reports`, `pipeline-artifacts`, …) |
| `OPENAI_ENDPOINT` | Foundry AI Services account (`https://<account>.cognitiveservices.azure.com/`) |
| `OPENAI_EMBEDDING_DEPLOYMENT` | Embedding deployment (text-embedding-3-large) |
| `OPENAI_GPT_DEPLOYMENT` / `OPENAI_GPT_MODEL_NAME` | Chat deployment used by the domain classifier and the run-analysis agent, and its model name |
| `CONTENT_SAFETY_ENDPOINT` | Prompt Shields (prompt-injection guard) — same Foundry account |
| `LANGUAGE_ENDPOINT` | Azure AI Language — PII detection (query side) and language detection (indexing side) — same Foundry account |

### Required for indexing only

| Setting | Purpose |
|---|---|
| `CONTENT_UNDERSTANDING_ENDPOINT` | Content Understanding, the only extraction backend. Optional at the `Infrastructure` level so a query-only host can start; `AddIndexing` throws without it |

### Optional, with defaults

| Setting | Default | Purpose |
|---|---|---|
| `STORAGE_CONTAINER` | `protocols` | Container the run-report assembler counts the corpus in. Note the extraction pipeline itself reads the `documents` container (hard-wired in `Indexing.CU/ServiceCollectionExtensions.cs`) |
| `OPENAI_EXTRACTION_DEPLOYMENT` | `gpt-41-extraction` | Legacy; not used by the Content Understanding path |
| `OPENAI_EMBEDDING_MODEL_NAME` | `text-embedding-3-large` | Model name written into snapshots/reports and verified in the CU default mapping |
| `OPENAI_EMBEDDING_DIMENSIONS` | `3072` | Vector size of the index field |
| `OPENAI_MINI_DEPLOYMENT` | `gpt-4.1-mini` | The **deployment name** Content Understanding's prebuilt analyzer resolves its completion model against (serves gpt-5.4-mini since 2026-08-27; the name was kept stable). Used only by `ContentUnderstandingDefaultsSetup` |
| `RunAnalysis__Enabled` | `true` | Master switch for the per-run `run-analysis` blob |
| `RunAnalysis__CalibrationMode` | `true` | While true, flags with uncalibrated thresholds render their value but do not fire |
| `WEBSITE_TIME_ZONE` | — | Set to `W. Europe Standard Time` in `function_app.tf` so the 17:00 timer is Dutch wall-clock time |

Not a setting: `IndexerConfig.GuardsLogOnly` (guards log but never block) is hard-coded `true`
and is not read from `GUARDS_LOG_ONLY` despite the comment on the property — see the root
README's [Known gaps](ReadMe.md#known-gaps-as-of-2026-09-11).

### Getting dev values

`infra/function_app.tf` (`app_settings`) shows how every deployed value is derived, or read them
off the running app:

```
az functionapp config appsettings list -g con-cap-data-dev-we-001 -n con-func-idx-cap-dev-we-001 -o table
```

`local.settings.json` is **not** in `.gitignore` — keep it out of commits. The two
`appsettings*.json` files in `AgenticRagApp.FunctionApp` are not added as a configuration
source by `Program.cs` (it uses `new HostBuilder().ConfigureFunctionsWorkerDefaults()` with no
`ConfigureAppConfiguration`); treat them as reference only.

## Running the Functions host

```
cd src/AgenticRagApp.FunctionApp
func start
```

Then `POST http://localhost:7071/api/index?force=true` etc. — see the endpoint table in the
[root README](ReadMe.md#endpoints). A local run writes real reports to the configured
`pipeline-reports` container and real Content Understanding calls are billed per page.

## Running the Zenya sync tool

`src/AgenticRagApp.Tools.ZenyaSync` is a console host over `Infrastructure/Clients/Zenya/Sync`.
It reads `ZENYA_BASE_URL`, `ZENYA_CLIENT_ID`, `ZENYA_ENTRA_SCOPE` (or `ZENYA_CLIENT_SECRET`),
`STORAGE_ACCOUNT_URL`, `STORAGE_CONTAINER` and `ZENYA_SYNC_DRY_RUN` (default `true`) from the
environment. Zenya validates the caller's Entra token by tenant + **appid** of the trusted
managed identity, so a developer's `az login` token is rejected (exit code 2). Run it through
`.pipelines/base/zenya-document-sync.yml` (`runSync=true`, `dryRun=true` first). Locally the
tool is useful for its 14 unit tests (`AgenticRagApp.Infrastructure.Tests`), not for a live run.

## Sample data

`data/chatbot-51pdf-documenten/` is the 51-document sample corpus every run review in `docs/`
measures against. `.pipelines/5-upload-sample-pdfs.yml` uploads it into the dev `documents`
container (it does not start an indexing run).
