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
