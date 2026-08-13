# ---------------------------------------------------------------------------
# Private path from the func subnet into App Insights, via Azure Monitor
# Private Link Scope (AMPLS). The indexer's OpenTelemetry exporters
# (Program.cs's AddAzureMonitor*Exporter calls) currently have to reach
# westeurope-5.in.applicationinsights.azure.com over the public internet -
# vnet_route_all_enabled on the Function App (function_app.tf) forces that
# egress through the hub firewall (network.tf's 0.0.0.0/0 UDR to the spoke
# route table), the same way it would for Storage/KeyVault/Search/Foundry if
# those didn't already have private endpoints. Confirmed 2026-08-07: App
# Insights is receiving zero telemetry (az monitor app-insights query, 48h
# window) despite the exporters being live - this is the same class of
# silent-failure the console-exporter diagnostics in Program.cs were added to
# investigate.
#
# Public ingestion/query on con-appi-cap-* is left untouched
# (data.azurerm_application_insights.main, publicNetworkAccessForIngestion:
# Enabled) - this only adds a second, private path via longest-prefix-match
# routing (same mechanism as every other private endpoint in this repo), so
# nothing that currently depends on public access (portal, ad-hoc az cli
# queries) breaks.
#
# See docs/2608/260807/app-insights-private-link.md.
# ---------------------------------------------------------------------------

resource "azurerm_monitor_private_link_scope" "main" {
  name                = "con-ampls-cap-${local.env}-${local.region}-${local.instance}"
  resource_group_name = data.azurerm_resource_group.ai.name

  tags = local.common_tags
}

# Links App Insights into the scope - required before its telemetry can flow
# over the private endpoint below.
resource "azurerm_monitor_private_link_scoped_service" "app_insights" {
  name                = "con-ampls-svc-appi-cap-${local.env}-${local.region}-${local.instance}"
  resource_group_name = data.azurerm_resource_group.ai.name
  scope_name          = azurerm_monitor_private_link_scope.main.name
  linked_resource_id  = data.azurerm_application_insights.main.id
}

# App Insights' IngestionMode is LogAnalytics, so per Microsoft's AMPLS docs
# the backing workspace should ALSO be linked into this scope for a
# fully-private path - deliberately NOT done here. Confirmed 2026-08-07: the
# deploy SP has zero access in that workspace's subscription
# (the log analytics/management subscription), not even enough for the
# azurerm.logmgmt provider to initialize (Microsoft.Resources/subscriptions/
# providers/read 403s at the subscription scope). Not needed for the actual
# bug this file fixes, though: Program.cs's OTel exporters
# (AddAzureMonitor*Exporter) call APPLICATIONINSIGHTS_CONNECTION_STRING's own
# ingestion endpoint, i.e. the App Insights resource's private link below -
# never the Log Analytics workspace directly. The gap this omission leaves is
# narrower: anything that needs to reach the *workspace itself* (not via App
# Insights) over the private path wouldn't have one. Add it back (see
# providers.tf's azurerm.logmgmt alias, still in place) once the platform
# team grants subscription-scope Reader there - see
# docs/2608/260807/app-insights-private-link.md.

# One private endpoint for the whole scope (not one per linked resource) -
# AMPLS's "azuremonitor" subresource fronts every service linked into it via
# the scoped-service resources above.
resource "azurerm_private_endpoint" "ampls" {
  name                          = "con-pep-ampls-cap-${local.env}-${local.region}-${local.instance}"
  location                      = var.location
  resource_group_name           = data.azurerm_resource_group.ai.name
  subnet_id                     = data.azurerm_subnet.pe.id
  custom_network_interface_name = "con-pep-ampls-cap-${local.env}-${local.region}-${local.instance}_nic"

  private_service_connection {
    name                           = "con-pep-ampls-cap-${local.env}-${local.region}-${local.instance}-psc"
    private_connection_resource_id = azurerm_monitor_private_link_scope.main.id
    subresource_names              = ["azuremonitor"]
    is_manual_connection           = false
  }

  # No private_dns_zone_group here, unlike this repo's other private
  # endpoints (storage.tf, search.tf, keyvault.tf, function_app.tf) - those
  # all reference zones this SP already has an individual
  # Private DNS Zone Contributor grant on (data.tf's comment, confirmed
  # 2026-07-07). The four AMPLS zones do not have that grant yet (confirmed
  # 2026-08-07: 403 AuthorizationFailed reading each one, not 404 - the
  # zones exist, this SP just isn't granted on them - see
  # docs/2608/260807/app-insights-private-link.md). A data source read
  # requires that grant regardless of whether its result is even used, so
  # there is no way to reference these zones from Terraform yet at all.
  #
  # Deliberately deferring DNS registration to the platform team's own
  # automatic remediation - the same mechanism that created the zones
  # themselves is expected to also populate the zone group/A records, under
  # its own identity, independent of this SP's access. If that doesn't
  # happen, add the zone-group block back once the platform team confirms
  # the per-zone grant (see the doc above for the exact ask), or register
  # the A records by hand as an interim step.
  tags = local.common_tags
}
