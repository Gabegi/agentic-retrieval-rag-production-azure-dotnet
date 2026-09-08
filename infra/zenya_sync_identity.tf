# User-assigned identity for the Zenya document sync (docs/2608/260828/zenya-api-ingestion-plan.md).
# Zenya's app registration trusts an azure_tenant_id + azure_client_id pair and validates that the
# client_assertion JWT was issued by Entra for that app - so client_id is what a Zenya admin needs.
#   - User-assigned, not system-assigned: it exists before the sync Function App does, so the Zenya
#     registration request can go out while the app is still being written.
#   - It outlives the app: a system-assigned identity is destroyed with its Function App and comes
#     back with a new client_id, silently breaking auth. This one can also attach to a second
#     resource later without re-registering.
#   - Holds no role assignments here; it gets grants where that access is defined (blob write on
#     the documents container).
resource "azurerm_user_assigned_identity" "zenya_sync" {
  name                = "con-id-zenyasync-cap-${local.env}-${local.region}-${local.instance}"
  location            = var.location
  resource_group_name = data.azurerm_resource_group.data.name

  tags = local.common_tags
}

# The one value a Zenya admin needs. Output rather than portal lookup: client_id and principal_id
# are different values, and Zenya's error for the wrong one is a bare "invalid client assertion"
# (measured 2026-08-28) - a mix-up would be expensive to diagnose.
output "zenya_sync_identity_client_id" {
  description = "Client (application) ID of the Zenya sync managed identity - give this to the Zenya administrator as azure_client_id on the app registration. NOT the principal/object id."
  value       = azurerm_user_assigned_identity.zenya_sync.client_id
}

output "zenya_sync_identity_principal_id" {
  description = "Principal (object) ID of the Zenya sync managed identity - used for Azure RBAC role assignments, not by Zenya."
  value       = azurerm_user_assigned_identity.zenya_sync.principal_id
}
