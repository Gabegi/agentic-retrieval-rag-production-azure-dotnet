# Development-only grants to fixed object IDs, so they don't move when a human runs apply.
# eval_access.tf grants to data.azurerm_client_config.current - fine for the CI service
# connection, but a local dev apply would silently move those roles onto the human and revoke
# the service principal's.
#   - Everything gates on var.environment == "development" (see variables.tf's dev_allowed_ips
#     for why that is safe even if dev values leak into prod.tfvars).
#   - The dev_eval_spn grants are skipped when the eval SPN IS the identity running apply (dev's
#     deploy service connection and dev_eval_service_principal_object_id are the same SPN):
#     eval_access.tf already grants that identity, and a duplicate (scope, principal, role) 409s
#     with RoleAssignmentExists.

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

# Same role set as eval_access.tf (local.eval_role_grants), for the fixed eval SPN.
resource "azurerm_role_assignment" "dev_eval_spn" {
  for_each             = local.dev_eval_spn_needs_fixed_grant ? local.eval_role_grants : {}
  scope                = each.value.scope
  role_definition_name = each.value.role
  principal_id         = var.dev_eval_service_principal_object_id
}
