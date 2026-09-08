# Landing-zone resources referenced read-only. Nothing in this file is created or modified by this
# configuration.

# --- Networking (platform/network team) ---------------------------------------

data "azurerm_resource_group" "network" {
  name = "con-cap-network-${local.env}-${local.region}-${local.instance}"
}

data "azurerm_virtual_network" "main" {
  name                = "con-vnet-cap-${local.env}-${local.region}-${local.instance}"
  resource_group_name = data.azurerm_resource_group.network.name
}

data "azurerm_subnet" "pe" {
  name                 = "con-snet-cap-pe-${local.instance}"
  virtual_network_name = data.azurerm_virtual_network.main.name
  resource_group_name  = data.azurerm_resource_group.network.name
}

data "azurerm_route_table" "spoke" {
  name                = "con-rt-spoke-cap-${local.env}-${local.region}-${local.instance}"
  resource_group_name = data.azurerm_resource_group.network.name
}

# --- Foundry / AI -------------------------------------------------------------

data "azurerm_resource_group" "ai" {
  name = "con-cap-ai-${local.env}-${local.region}-${local.instance}"
}

data "azurerm_cognitive_account" "foundry" {
  name                = "con-ais-cap-${local.env}-${local.region}-${local.instance}"
  resource_group_name = data.azurerm_resource_group.ai.name
}

data "azurerm_application_insights" "main" {
  name                = "con-appi-cap-${local.env}-${local.region}-${local.instance}"
  resource_group_name = data.azurerm_resource_group.ai.name
}

# --- Data tier ----------------------------------------------------------------

data "azurerm_resource_group" "data" {
  name = "con-cap-data-${local.env}-${local.region}-${local.instance}"
}

# --- Private DNS zones (hub/connectivity subscription, platform team) ---
# The SP holds Private DNS Zone Contributor on each zone individually (confirmed 2026-07-07), so our
# private endpoints attach a private_dns_zone_group directly instead of waiting on policy-based
# remediation.
#   - queue/table/search zones were created by the platform team 2026-07-08
#     (docs/2607/260720/platform-team-dns-verzoek.md).
#   - The RG is a plain string, not a data source: the SP has no read on the RG object itself (the
#     grant is zone-scoped), and a data source read would 403.
#   - The four AMPLS zones (privatelink.monitor.azure.com, .oms/.ods.opinsights.azure.com,
#     .agentsvc.azure-automation.net) are deliberately NOT declared: the SP has no grant on them
#     (403 AuthorizationFailed on 2026-08-07, not 404), and a data source is read at plan time even
#     if unreferenced. See app_insights_privatelink.tf and
#     docs/2608/260807/app-insights-private-link.md.
#   - The Log Analytics workspace behind App Insights is not declared either: providers.tf's
#     azurerm.logmgmt alias cannot even initialize in that subscription (zero access, 2026-08-07).
#     Re-add once the platform team grants subscription-scope Reader there.

locals {
  dns_hub_resource_group_name = "example-connectivity-dns-prd-we-001"
}

data "azurerm_private_dns_zone" "azurewebsites" {
  provider            = azurerm.hub
  name                = "privatelink.azurewebsites.net"
  resource_group_name = local.dns_hub_resource_group_name
}

data "azurerm_private_dns_zone" "blob" {
  provider            = azurerm.hub
  name                = "privatelink.blob.core.windows.net"
  resource_group_name = local.dns_hub_resource_group_name
}

data "azurerm_private_dns_zone" "file" {
  provider            = azurerm.hub
  name                = "privatelink.file.core.windows.net"
  resource_group_name = local.dns_hub_resource_group_name
}

data "azurerm_private_dns_zone" "vaultcore" {
  provider            = azurerm.hub
  name                = "privatelink.vaultcore.azure.net"
  resource_group_name = local.dns_hub_resource_group_name
}

data "azurerm_private_dns_zone" "queue" {
  provider            = azurerm.hub
  name                = "privatelink.queue.core.windows.net"
  resource_group_name = local.dns_hub_resource_group_name
}

data "azurerm_private_dns_zone" "table" {
  provider            = azurerm.hub
  name                = "privatelink.table.core.windows.net"
  resource_group_name = local.dns_hub_resource_group_name
}

data "azurerm_private_dns_zone" "search" {
  provider            = azurerm.hub
  name                = "privatelink.search.windows.net"
  resource_group_name = local.dns_hub_resource_group_name
}
