# ---------------------------------------------------------------------------
# Data-plane access for the golden-questions eval suite (RagApp.Evaluation.Tests,
# run by .pipelines/base/run-eval-tests.yml and the Evaluate stage in
# .pipelines/pipeline.yml). That pipeline authenticates as the same
# deployer identity used for Plan/Apply/Deploy (data.azurerm_client_config.current,
# see keyvault.tf's kv_admin_deployer) via DefaultAzureCredential's
# AzureCliCredential fallback - not the Function/API apps' own managed
# identities, which already have their own roles in function_app.tf/
# app_service.tf and are irrelevant here.
#
# Mirrors the existing search_openai_user pattern in search.tf. These role
# assignments only fix authorization - network reachability from a hosted
# Azure Pipelines agent is handled separately, at runtime, by the
# 'Open network access for eval run' / 'Close network access after eval run'
# steps in .pipelines/pipeline.yml's Evaluate stage (and the standalone copy
# in .pipelines/base/run-eval-tests.yml), which temporarily allowlist that
# run's IP on Search, Storage, and the Foundry account. See
# infra/dev_access.tf for the parallel, stable (non-`current`-based) grants
# to this same service principal, kept for dev applies run by a human.
# ---------------------------------------------------------------------------

resource "azurerm_role_assignment" "eval_search_index_data_reader" {
  scope                = azurerm_search_service.main.id
  role_definition_name = "Search Index Data Reader"
  principal_id         = data.azurerm_client_config.current.object_id
}

resource "azurerm_role_assignment" "eval_openai_user" {
  scope                = data.azurerm_cognitive_account.foundry.id
  role_definition_name = "Cognitive Services OpenAI User"
  principal_id         = data.azurerm_client_config.current.object_id
}

# RagEvaluationTests.cs builds a real AgenticRagQueryService including
# PromptInjectionGuard/PiiGuard, which call Content Safety (Prompt Shields) and
# AI Language (PII) on the same Foundry account - "Cognitive Services OpenAI
# User" above only covers the OpenAI/*.action data actions, not
# ContentSafety/*.action or Language/*.action, so a separate, broader grant is
# needed. "Cognitive Services User"'s dataActions is the wildcard
# Microsoft.CognitiveServices/* - confirmed via `az role definition list`.
resource "azurerm_role_assignment" "eval_cognitive_services_user" {
  scope                = data.azurerm_cognitive_account.foundry.id
  role_definition_name = "Cognitive Services User"
  principal_id         = data.azurerm_client_config.current.object_id
}

# EvalResultWriter (RagApp.Evaluation.Tests) appends JSONL results as blobs
# into azurerm_storage_container.eval_results (storage.tf) - this grants the
# data-plane access needed to write to it.
resource "azurerm_role_assignment" "eval_storage_blob_data_contributor" {
  scope                = azurerm_storage_account.data.id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = data.azurerm_client_config.current.object_id
}
