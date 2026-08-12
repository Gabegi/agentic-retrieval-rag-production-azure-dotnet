# ---------------------------------------------------------------------------
# Existing landing zone resources, referenced read-only via data sources.
# Nothing in this file is created or modified by this Terraform config.
# ---------------------------------------------------------------------------

# --- Networking (owned by the platform/network team) ------------------------

data "azurerm_resource_group" "network" {
  name = "cor-cap-network-${local.env}-${local.region}-${local.instance}"
}

data "azurerm_virtual_network" "main" {
  name                = "cor-vnet-cap-${local.env}-${local.region}-${local.instance}"
  resource_group_name = data.azurerm_resource_group.network.name
}

data "azurerm_subnet" "pe" {
  name                 = "cor-snet-cap-pe-${local.instance}"
  virtual_network_name = data.azurerm_virtual_network.main.name
  resource_group_name  = data.azurerm_resource_group.network.name
}

data "azurerm_route_table" "spoke" {
  name                = "cor-rt-spoke-cap-${local.env}-${local.region}-${local.instance}"
  resource_group_name = data.azurerm_resource_group.network.name
}

# --- Foundry / AI -------------------------------------------------------------

data "azurerm_resource_group" "ai" {
  name = "cor-cap-ai-${local.env}-${local.region}-${local.instance}"
}

data "azurerm_cognitive_account" "foundry" {
  name                = "cor-ais-cap-${local.env}-${local.region}-${local.instance}"
  resource_group_name = data.azurerm_resource_group.ai.name
}

data "azurerm_application_insights" "main" {
  name                = "cor-appi-cap-${local.env}-${local.region}-${local.instance}"
  resource_group_name = data.azurerm_resource_group.ai.name
}

# --- Data tier ------------------------------------------------------------

data "azurerm_resource_group" "data" {
  name = "cor-cap-data-${local.env}-${local.region}-${local.instance}"
}

# --- Private DNS zones (hub, owned by platform team) -----------------------
# Hub/connectivity subscription, private DNS resource group.
# Confirmed via pipeline diagnostic (2026-07-07): the SP has Private DNS
# Zone Contributor scoped individually on these zones, so their private
# endpoints attach a private_dns_zone_group directly (search.tf, storage.tf,
# keyvault.tf, function_app.tf) rather than waiting on the platform team's
# policy-based remediation. privatelink.queue/table.core.windows.net and
# privatelink.search.windows.net were created by the platform team on
# 2026-07-08 (docs/platform-team-dns-verzoek.md); the stfunc_queue/
# stfunc_table/search private endpoints now attach zone groups too.

# A data "azurerm_resource_group" here would need
# Microsoft.Resources/subscriptions/resourceGroups/read on the RG itself -
# the SP's hub access only covers the DNS zones (Private DNS Zone
# Contributor scoped to the zones, see above), not the RG object, so that
# 403s. Plain string instead; each zone data source below only needs
# zone-level read.
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

# --- Azure Monitor Private Link (AMPLS) zones -------------------------------
# Deliberately NOT declared as data sources here, unlike every zone above.
# app_insights_privatelink.tf's private endpoint needs
# privatelink.monitor.azure.com, .oms.opinsights.azure.com,
# .ods.opinsights.azure.com, and .agentsvc.azure-automation.net (plus
# privatelink.blob.core.windows.net, already covered above) per Microsoft's
# AMPLS DNS requirements - but confirmed 2026-08-07 that this SP has no grant
# on any of them (403 AuthorizationFailed reading each one, not 404 - the
# zones exist, just not individually granted to this SP the way the zones
# above were). A Terraform data source is read at plan time regardless of
# whether anything references its result, so even an unused declaration here
# would 403 the same way - there is no way to reference these zones from
# Terraform until that grant exists. See
# docs/2608/260807/app-insights-private-link.md for the exact platform-team
# ask; app_insights_privatelink.tf's private endpoint omits its
# private_dns_zone_group for the same reason and defers DNS registration to
# the platform team's own automation in the meantime.

# Log Analytics workspace backing App Insights - deliberately NOT declared
# here. Would need providers.tf's azurerm.logmgmt alias (still in place,
# unused for now) - confirmed 2026-08-07 that alias can't even initialize,
# let alone read this workspace (zero access in that subscription). Not
# needed for app_insights_privatelink.tf's actual fix - see its comment on
# why the Log Analytics scoped-service link was dropped rather than waited
# on. Re-add once the platform team grants subscription-scope Reader there
# (docs/2608/260807/app-insights-private-link.md).
