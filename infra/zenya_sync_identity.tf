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

# Zenya's registration also pins azure_tenant_id; nothing in the repo records our tenant (terraform
# reads it from the credential), so surface it next to the client id.
output "zenya_sync_identity_tenant_id" {
  description = "Entra tenant the Zenya sync identity lives in - give this to the Zenya administrator as azure_tenant_id, together with the client id."
  value       = data.azurerm_client_config.current.tenant_id
}

# --- Track A: let an ADO workload-identity service connection run AS this identity --------------
# (docs/2609/260910/zenya-ingestion-execution-steps.md, step A2.) A federated credential per
# service connection: ADO presents an Entra-issued token (issuer login.microsoftonline.com/<ado
# tenant>/v2.0, subject the /eid1/... path shown in the new-connection dialog), Entra exchanges it
# for a token of this identity. No secret is created anywhere. Both hosts - the ADO agent via this
# credential and, later, the sync Function App via direct attachment - then present the same
# client_id, which is what keeps Zenya to one registration.
#
# Issuer + subject come from the ADO dialog and are pinned in <env>.tfvars: they identify one
# service connection in one ADO project and are not secrets. The connection name is embedded in
# the subject on ADO's side, so renaming the connection means a new entry here.
resource "azurerm_federated_identity_credential" "zenya_sync_ado" {
  for_each = var.zenya_sync_ado_federated_credentials

  # No resource_group_name: azurerm 4.81 reports it deprecated and unused (the parent identity's
  # id carries the RG) - measured as a plan warning on the first apply, 2026-09-10.
  name      = each.key
  parent_id = azurerm_user_assigned_identity.zenya_sync.id
  audience  = ["api://AzureADTokenExchange"]
  issuer    = each.value.issuer
  subject   = each.value.subject
}

# ADO's "Verify" on the connection reads the subscription through ARM; any role assignment inside
# the subscription makes it visible, so Reader on the data RG (where this identity's resources
# live) is enough and stays narrower than a subscription-scope grant. Blob contributor on the data
# account is what the sync itself needs: it writes the documents container (storage.tf) from the
# ADO agent in Track A and from the Function App in Track B - same identity, one grant.
resource "azurerm_role_assignment" "zenya_sync_reader_data_rg" {
  scope                = data.azurerm_resource_group.data.id
  role_definition_name = "Reader"
  principal_id         = azurerm_user_assigned_identity.zenya_sync.principal_id
  principal_type       = "ServicePrincipal"
}

resource "azurerm_role_assignment" "zenya_sync_blob_contributor" {
  scope                = azurerm_storage_account.data.id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = azurerm_user_assigned_identity.zenya_sync.principal_id
  principal_type       = "ServicePrincipal"
}
