# Eval Suite — Identity & RBAC

## Identity

All Azure clients built in `RagEvaluationTests.ClassInit` share a single `DefaultAzureCredential()`
(`RagEvaluationTests.cs`). In the pipeline this resolves via `AzureCliCredential`, after the
`az login --service-principal ...` step in `.pipelines/pipeline.yml`'s Evaluate stage — i.e. the
eval suite authenticates as **the same deployer identity that runs Terraform plan/apply/deploy**
(`data.azurerm_client_config.current`), not a separate managed identity. See `infra/eval_access.tf`'s
header comment for the same note from the infra side.

## Roles needed, by call site

| Client / call | Code | Role needed | Terraform grant |
|---|---|---|---|
| `AzureOpenAIClient` — RAG query + all judge calls | `RagEvaluationTests.cs`, `RagEvaluator.cs`, `RefusalEvaluator.cs` | Cognitive Services OpenAI User (on the Foundry account) | `infra/eval_access.tf` → `eval_openai_user` |
| `SearchClient.GetDocumentCountAsync` — index health check | `RagEvaluationTests.cs` | Search Index Data Reader | `infra/eval_access.tf` → `eval_search_index_data_reader` |
| `KnowledgeBaseRetrievalClient` — agentic retrieval query | via `KnowledgeBaseClient`, used by `AgenticRagQueryService` | Search Index Data Reader (data-plane query on the knowledge base) | same grant as above |
| `SearchIndexClient.CreateOrUpdateKnowledgeSourceAsync` / `...KnowledgeBaseAsync` — **write**, not read | `KnowledgeService.cs`, called from `ClassInit` | Unclear — `Search Index Data Reader` is read-only by name; creating/updating a knowledge source or knowledge base is a management-style write. Not otherwise covered by any Terraform grant to this identity. | Only `Search Index Data Reader` is declared for this identity anywhere in `infra/eval_access.tf` / `infra/dev_access.tf` |
| `EvalResultWriter.WriteAsync` — append-blob create/write | `EvalResultWriter.cs` | Storage Blob Data Contributor | `infra/eval_access.tf` → `eval_storage_blob_data_contributor` |

## All principals with access to the Foundry account (dev)

The eval SPN isn't the only identity granted on `con-ais-cap-dev-we-001` (resource group
`con-cap-ai-dev-we-001`, subscription `con-cap-dev`). Pulled via `az role assignment list --scope
<foundry-account-id>` on 2026-08-06 — re-run that command rather than trusting this table if it's
been a while, since grants can drift independently of Terraform (e.g. manual portal changes):

| Principal | Role | Source (Terraform) |
|---|---|---|
| Azure AI Search service identity | Cognitive Services OpenAI User | `infra/search.tf` → `search_openai_user` |
| Function App `indexer` identity | Cognitive Services OpenAI User | `infra/function_app.tf` → `openai_user` |
| Function App `indexer` identity | Cognitive Services User | `infra/content_understanding.tf` → `func_content_understanding_user` |
| CI/CD deploy pipeline SPN (`data.azurerm_client_config.current`) | Cognitive Services OpenAI User | `infra/eval_access.tf` → `eval_openai_user` (this is the eval suite's own identity, see above) |
| App Service `api` identity | Cognitive Services OpenAI User | `infra/app_service.tf` → `openai_user` |

A conditional grant for a developer's local eval service principal (`infra/dev_access.tf` →
`dev_eval_spn_openai_user`, keyed by `var.dev_eval_service_principal_object_id`) also exists in
code but is currently inactive — `local.dev_eval_spn_needs_fixed_grant` evaluates false, so it
doesn't show up in the live list above.

Service principal display names didn't resolve (`az ad sp show` returned "Insufficient
privileges" for this session's identity against Graph) — the mapping above is by cross-referencing
each `principalId`'s scope/role/creation-time against the Terraform resource that provisions the
matching identity, not by resolved name.

## Open questions / known gaps

1. **Knowledge source/base writes run on a read-only-named role.** `EnsureKnowledgeSourceAsync`/
   `EnsureKnowledgeBaseAsync` succeed in observed runs despite only `Search Index Data Reader`
   being granted — either that role covers more than its name suggests for these (newer, preview)
   resource types, or something else is covering it. Treat as unverified, not confirmed-fine;
   check against Azure's Search RBAC docs for knowledge sources/bases rather than inferring from
   one passing run.

2. **Storage `AuthorizationFailure` (403) on `EvalResultWriter.WriteAsync`** — confirmed live via
   `az rest` against the roleAssignments API in dev (`constdatacapdevwe`): the deployer SP has
   `Storage Blob Data Contributor` directly on the storage account. Not yet confirmed in prod
   (`constdatacapprdwe`) — no CLI visibility into that resource group from this session. If it
   recurs after confirming the role is live, also rule out an IP-firewall rejection: Azure Storage
   returns the same `AuthorizationFailure` error for a blocked IP as for a missing RBAC role, and
   the pipeline's "Open network access for eval run" step needs to have actually landed the
   runner's IP on the storage account before the tests ran, not just that the role exists.

3. **Content-filter blocks on judge calls** (not just the RAG call) can raise inside
   `RagEvaluator.RunAsync`'s scoring phase (e.g. `RefusalEvaluator` grading a prompt-injection
   query, whose grading prompt embeds the harmful text and can itself trip Azure OpenAI's content
   filter). This is now caught and scored — see `RagEvaluator.cs`'s `RunAsync`/`DescribeError` —
   rather than crashing the test as an unhandled exception. `DescribeError` also pulls the raw
   HTTP response body via `GetRawResponse()` so `EvalRow.Error`/`RefusalRationale` carries the
   actual blocked-for-reason detail instead of a bare `HTTP 400 (: content_filter)`.
