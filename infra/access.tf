# Data-plane role assignments for identities that are neither the apps' managed identities
# (function_app.tf, app_service.tf) nor granted by the landing zone: the eval pipeline's deployer
# identity, a fixed eval SPN, and named developers.
#
# Eval suite (RagApp.Evaluation.Tests, run by .pipelines/base/run-eval-tests.yml and
# pipeline.yml's Evaluate stage) runs as the deployer identity
# (data.azurerm_client_config.current), not a managed identity.
#   - Authorization only. Network reachability from the hosted agent is opened and closed at
#     runtime by the pipeline's 'Open/Close network access for eval run' steps (Search, Storage,
#     Foundry).
#   - "Cognitive Services OpenAI User" covers only OpenAI/*.action. PromptInjectionGuard/PiiGuard
#     call Content Safety and AI Language on the same account, so "Cognitive Services User"
#     (dataActions Microsoft.CognitiveServices/*) is granted as well.
#   - Blob contributor: EvalResultWriter writes JSONL results into the data account.
#
# Development-only grants go to fixed object IDs so they don't move when a human runs apply:
# granting to data.azurerm_client_config.current is fine for the CI service connection, but a
# local dev apply would silently move those roles onto the human and revoke the service
# principal's.
#   - Everything gates on var.environment == "development" (see variables.tf's dev_allowed_ips
#     for why that is safe even if dev values leak into prod.tfvars).
#   - The dev_eval_spn grants are skipped when the eval SPN IS the identity running apply (dev's
#     deploy service connection and dev_eval_service_principal_object_id are the same SPN):
#     azurerm_role_assignment.eval already grants that identity, and a duplicate
#     (scope, principal, role) 409s with RoleAssignmentExists.

locals {
  # One role set, two principals: the deployer (azurerm_role_assignment.eval) and the fixed eval
  # SPN (azurerm_role_assignment.dev_eval_spn).
  eval_role_grants = {
    search_index_reader = {
      scope = azurerm_search_service.main.id
      role  = "Search Index Data Reader"
    }
    openai_user = {
      scope = data.azurerm_cognitive_account.foundry.id
      role  = "Cognitive Services OpenAI User"
    }
    cognitive_services_user = {
      scope = data.azurerm_cognitive_account.foundry.id
      role  = "Cognitive Services User"
    }
    storage_blob_contributor = {
      scope = azurerm_storage_account.data.id
      role  = "Storage Blob Data Contributor"
    }
  }

  dev_eval_spn_needs_fixed_grant = (
    var.environment == "development" &&
    var.dev_eval_service_principal_object_id != "" &&
    var.dev_eval_service_principal_object_id != data.azurerm_client_config.current.object_id
  )
}

resource "azurerm_role_assignment" "eval" {
  for_each             = local.eval_role_grants
  scope                = each.value.scope
  role_definition_name = each.value.role
  principal_id         = data.azurerm_client_config.current.object_id
}

resource "azurerm_role_assignment" "dev_eval_spn" {
  for_each             = local.dev_eval_spn_needs_fixed_grant ? local.eval_role_grants : {}
  scope                = each.value.scope
  role_definition_name = each.value.role
  principal_id         = var.dev_eval_service_principal_object_id
}

resource "azurerm_role_assignment" "dev_developer_search_reader" {
  for_each             = var.environment == "development" ? toset(var.dev_developer_object_ids) : []
  scope                = azurerm_search_service.main.id
  role_definition_name = "Search Index Data Reader"
  principal_id         = each.value
}
