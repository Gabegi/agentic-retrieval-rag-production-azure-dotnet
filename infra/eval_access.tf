# Data-plane access for the golden-questions eval suite (RagApp.Evaluation.Tests, run by
# .pipelines/base/run-eval-tests.yml and pipeline.yml's Evaluate stage). The pipeline runs as the
# deployer identity (data.azurerm_client_config.current), not the apps' managed identities.
#   - Authorization only. Network reachability from the hosted agent is opened and closed at
#     runtime by the pipeline's 'Open/Close network access for eval run' steps (Search, Storage,
#     Foundry).
#   - dev_access.tf grants the same role set to a fixed SPN object ID, so a dev apply by a human
#     doesn't move these grants onto the human.
#   - "Cognitive Services OpenAI User" covers only OpenAI/*.action. PromptInjectionGuard/PiiGuard
#     call Content Safety and AI Language on the same account, so "Cognitive Services User"
#     (dataActions Microsoft.CognitiveServices/*) is granted as well.
#   - Blob contributor: EvalResultWriter writes JSONL results into the data account.

locals {
  # One role set, two principals: the deployer here, the fixed eval SPN in dev_access.tf.
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
}

resource "azurerm_role_assignment" "eval" {
  for_each             = local.eval_role_grants
  scope                = each.value.scope
  role_definition_name = each.value.role
  principal_id         = data.azurerm_client_config.current.object_id
}
