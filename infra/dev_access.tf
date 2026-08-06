# ---------------------------------------------------------------------------
# Development-only data-plane access, distinct from eval_access.tf's grants
# to data.azurerm_client_config.current (whichever identity ran the last
# apply). That's fine for the CI service connection but unstable the moment
# a human applies dev locally, since the next apply would silently move
# those roles onto the human's identity and revoke the service principal's.
#
# The grants here use fixed object IDs instead (var.dev_developer_object_ids,
# var.dev_eval_service_principal_object_id), so they don't shift regardless
# of who runs apply. Both gate on var.environment == "development" - see
# variables.tf's dev_allowed_ips for why that's safe to leave populated even
# if dev.tfvars values were accidentally reused in prod.tfvars.
#
# The dev_eval_spn_* grants below additionally skip creation when the eval
# SPN IS the identity currently running apply (dev.tfvars' deploy pipeline
# service connection and dev_eval_service_principal_object_id are the same
# SPN there) - eval_access.tf already grants these same roles to
# data.azurerm_client_config.current in that case, and a second identical
# role assignment 409s (RoleAssignmentExists).
# ---------------------------------------------------------------------------

locals {
  dev_eval_spn_needs_fixed_grant = (
    var.environment == "development" &&
    var.dev_eval_service_principal_object_id != "" &&
    var.dev_eval_service_principal_object_id != data.azurerm_client_config.current.object_id
  )
}

resource "azurerm_role_assignment" "dev_developer_search_reader" {
  for_each             = var.environment == "development" ? toset(var.dev_developer_object_ids) : []
  scope                = azurerm_search_service.main.id
  role_definition_name = "Search Index Data Reader"
  principal_id         = each.value
}

resource "azurerm_role_assignment" "dev_eval_spn_search_reader" {
  count                = local.dev_eval_spn_needs_fixed_grant ? 1 : 0
  scope                = azurerm_search_service.main.id
  role_definition_name = "Search Index Data Reader"
  principal_id         = var.dev_eval_service_principal_object_id
}

resource "azurerm_role_assignment" "dev_eval_spn_storage_contributor" {
  count                = local.dev_eval_spn_needs_fixed_grant ? 1 : 0
  scope                = azurerm_storage_account.data.id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = var.dev_eval_service_principal_object_id
}

resource "azurerm_role_assignment" "dev_eval_spn_openai_user" {
  count                = local.dev_eval_spn_needs_fixed_grant ? 1 : 0
  scope                = data.azurerm_cognitive_account.foundry.id
  role_definition_name = "Cognitive Services OpenAI User"
  principal_id         = var.dev_eval_service_principal_object_id
}

# Mirrors eval_access.tf's eval_cognitive_services_user - see that resource's
# comment. Needed here too since the eval pipeline's identity when it's NOT
# the one running apply (the common case) only gets fixed grants from this
# file, not eval_access.tf's data.azurerm_client_config.current-based ones.
resource "azurerm_role_assignment" "dev_eval_spn_cognitive_services_user" {
  count                = local.dev_eval_spn_needs_fixed_grant ? 1 : 0
  scope                = data.azurerm_cognitive_account.foundry.id
  role_definition_name = "Cognitive Services User"
  principal_id         = var.dev_eval_service_principal_object_id
}
