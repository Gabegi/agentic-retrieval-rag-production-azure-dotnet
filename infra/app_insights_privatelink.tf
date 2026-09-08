# Private path from the func subnet into App Insights via Azure Monitor Private Link Scope (AMPLS).
# Without it the indexer's OTel exporters (Program.cs AddAzureMonitor*Exporter) reach
# westeurope-5.in.applicationinsights.azure.com over the public internet through the hub firewall
# (vnet_route_all_enabled + the 0.0.0.0/0 UDR) - and App Insights was receiving zero telemetry
# (confirmed 2026-08-07, 48h query window).
#   - Public ingestion/query on con-appi-cap-* is left enabled; this only adds a second, private
#     path via longest-prefix-match routing, so portal and ad-hoc az queries keep working.
#   - See docs/2608/260807/app-insights-private-link.md.

resource "azurerm_monitor_private_link_scope" "main" {
  name                = "con-ampls-cap-${local.env}-${local.region}-${local.instance}"
  resource_group_name = data.azurerm_resource_group.ai.name

  tags = local.common_tags
}

# Links App Insights into the scope - required before telemetry can flow over the endpoint below.
resource "azurerm_monitor_private_link_scoped_service" "app_insights" {
  name                = "con-ampls-svc-appi-cap-${local.env}-${local.region}-${local.instance}"
  resource_group_name = data.azurerm_resource_group.ai.name
  scope_name          = azurerm_monitor_private_link_scope.main.name
  linked_resource_id  = data.azurerm_application_insights.main.id
}

# The backing Log Analytics workspace (IngestionMode: LogAnalytics) should ALSO be linked per
# Microsoft's AMPLS guidance - deliberately not done.
#   - The deploy SP has zero access in that subscription (log analytics/management); the
#     azurerm.logmgmt alias cannot even initialize (confirmed 2026-08-07, providers.tf).
#   - Not needed for the bug this file fixes: the exporters call the App Insights ingestion
#     endpoint, never the workspace directly. The remaining gap is only for anything that must reach
#     the workspace itself privately.
#   - Add it back once the platform team grants subscription-scope Reader
#     (docs/2608/260807/app-insights-private-link.md).

# One private endpoint for the whole scope - the "azuremonitor" subresource fronts every linked
# service.
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

  # No private_dns_zone_group, unlike every other private endpoint here: the SP has no grant on the
  # four AMPLS zones (403 AuthorizationFailed reading each, 2026-08-07 - see data.tf), and a data
  # source read needs that grant even if unused. DNS registration is deferred to the platform
  # team's automatic remediation; if that doesn't happen, add the zone group once the per-zone
  # grant lands, or register the A records by hand meanwhile.
  tags = local.common_tags
}
