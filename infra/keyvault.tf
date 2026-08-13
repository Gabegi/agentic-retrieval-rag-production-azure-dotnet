data "azurerm_client_config" "current" {}

resource "azurerm_key_vault" "main" {
  # Instance bumped to 002: "...-001" collides with an orphaned soft-deleted
  # vault (different RG, deleted 2026-06-24, purge-protected until
  # 2026-09-22) - names must be globally unique per region+sub even while
  # soft-deleted, and recovery only targets the original RG. Reclaim "001"
  # after the scheduled purge date if it matters.
  name                = "con-kv-cap-${local.env}-${local.region}-002"
  location            = var.location
  resource_group_name = data.azurerm_resource_group.data.name
  tenant_id           = data.azurerm_client_config.current.tenant_id
  sku_name            = "standard"

  rbac_authorization_enabled = true
  purge_protection_enabled   = true

  public_network_access_enabled = false

  tags = local.common_tags
}

resource "azurerm_private_endpoint" "kv" {
  name                          = "con-pep-kv-cap-${local.env}-${local.region}-${local.instance}"
  location                      = var.location
  resource_group_name           = data.azurerm_resource_group.data.name
  subnet_id                     = data.azurerm_subnet.pe.id
  custom_network_interface_name = "con-pep-kv-cap-${local.env}-${local.region}-${local.instance}_nic"

  private_service_connection {
    name                           = "con-pep-kv-cap-${local.env}-${local.region}-${local.instance}-psc"
    private_connection_resource_id = azurerm_key_vault.main.id
    subresource_names              = ["vault"]
    is_manual_connection           = false
  }

  private_dns_zone_group {
    name                 = "default"
    private_dns_zone_ids = [data.azurerm_private_dns_zone.vaultcore.id]
  }

  tags = local.common_tags
}

# Deploying identity gets vault management rights so secrets can be managed
# going forward (this is administrative bootstrap access for whoever runs
# Terraform, not workload runtime access - app identities get their own
# scoped role assignments, e.g. Key Vault Secrets User, when they're created).
resource "azurerm_role_assignment" "kv_admin_deployer" {
  scope                = azurerm_key_vault.main.id
  role_definition_name = "Key Vault Administrator"
  principal_id         = data.azurerm_client_config.current.object_id
}
