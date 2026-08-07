# Running Locally

## Prerequisites

- .NET 10 SDK
- Azure CLI (`az`), logged in (`az login`) as an identity with access to the dev resources (see `infra/envs/dev.tfvars`'s `dev_developer_object_ids` / `dev_allowed_ips` — your account/IP needs to be in there to reach Search, Storage, and the Function App directly)
- Access to the dev Function App/Storage/Search endpoints — either via VPN/on the platform network, or by temporarily allowlisting your IP (see [infra/Infrastructure.md](infra/Infrastructure.md#debugging-the-dev-function-app))

## Build & test

- Build: `dotnet build`
- Run unit tests: `dotnet test src/UnitTests`
- Run evals: see [Evaluations README](src/Evaluations/RagApp.Evaluation.Tests/README.md)

## Configuration

Local runs read configuration the same way the deployed Function App does (`AgenticRagApp.FunctionApp/Program.cs`) — via environment variables / `local.settings.json`, not committed secrets. At minimum you'll need:

| Setting | Purpose |
|---|---|
| `SEARCH_ENDPOINT` | Azure AI Search service used by indexing/querying |
| `STORAGE_ACCOUNT_URL` | Data storage account (documents, reports, snapshots) |
| `OPENAI_ENDPOINT` | Foundry AI Services account (OpenAI + Document Intelligence) |
| `OPENAI_EMBEDDING_DEPLOYMENT` / `OPENAI_GPT_DEPLOYMENT` / `OPENAI_EXTRACTION_DEPLOYMENT` | Model deployment names — see `infra/ai_deployments.tf` for current values |
| `SEARCH_INDEX_NAME` / `KNOWLEDGE_SOURCE_NAME` / `KNOWLEDGE_BASE_NAME` | Search index/knowledge base names |

Values matching the dev environment can be pulled from the deployed dev Function App's configuration (Azure Portal → Configuration, or `az functionapp config appsettings list`). Auth to Search/Storage/OpenAI is via `DefaultAzureCredential` (your `az login` identity in dev), not API keys.

## Sample data

`data/` holds sample PDFs for local indexing runs — see `.pipelines/5-upload-sample-pdfs.yml` for how they get uploaded to the dev environment in CI.
