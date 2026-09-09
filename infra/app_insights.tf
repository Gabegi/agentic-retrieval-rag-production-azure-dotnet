# Observability for both workloads (indexer Function App + query API): a Log Analytics workspace
# and the App Insights component that ingests into it, both created and owned by this
# configuration, plus the private path from the func subnet via Azure Monitor Private Link Scope
# (AMPLS).
#
# Self-owned on purpose (decided 2026-09-09). Previously this config read the landing-zone team's
# component (data.azurerm_application_insights.main -> con-appi-cap-<env>-we-001 in con-cap-ai-*,
# tagged owner: the landing-zone team, created 2026-06-24), which ingested into their workspace
# in the log analytics/management subscription where this deploy SP has zero access - not
# even enough to initialize providers.tf's azurerm.logmgmt alias (403 at subscription scope,
# 2026-08-07). Two consequences:
#   - No Terraform-visible workspace, so the AMPLS below could not link one, even though
#     Microsoft's AMPLS guidance wants the backing workspace linked alongside the component.
#   - A shared object: their pipeline writes its tags, so this config could only ever have adopted
#     it with lifecycle.ignore_changes and a standing risk of the two trading it.
# Owning both resources removes both problems and drops all dependency on that third subscription.
#
# Their component is deliberately NOT imported and NOT destroyed here. It keeps existing and keeps
# its history queryable in their workspace; it simply stops receiving telemetry once the app
# settings in function_app.tf/app_service.tf resolve to the component below.
#   - Same name as theirs, from naming.tf's usual convention, but in the RG this config creates
#     (resource_groups.tf): App Insights component names are unique per resource group, not
#     globally, so no suffix is needed to avoid the collision.

resource "azurerm_log_analytics_workspace" "main" {
  name                = "con-log-cap-${local.env}-${local.region}-${local.instance}"
  location            = var.location
  resource_group_name = azurerm_resource_group.api.name
  sku                 = "PerGB2018"

  # 90 days mirrors what the landing-zone component was set to (retentionInDays: 90, read
  # 2026-09-09), so switching workspaces does not quietly shorten the window. For a workspace-based
  # component this workspace setting is what actually governs retention.
  retention_in_days = 90

  # Public ingestion/query stay enabled, same call as for the component and the AMPLS below: the
  # private endpoint only ADDS a path (docs/2608/260807/app-insights-private-link.md).
  internet_ingestion_enabled = true
  internet_query_enabled     = true

  tags = local.common_tags
}

resource "azurerm_application_insights" "main" {
  name                = "con-appi-cap-${local.env}-${local.region}-${local.instance}"
  location            = var.location
  resource_group_name = azurerm_resource_group.api.name
  application_type    = "web"

  # Makes this a workspace-based component (ingestionMode: LogAnalytics). Required: Azure no longer
  # creates classic components. Now an in-config reference rather than a literal ARM ID.
  workspace_id = azurerm_log_analytics_workspace.main.id

  internet_ingestion_enabled = true
  internet_query_enabled     = true
  sampling_percentage        = 100

  tags = local.common_tags

  # application_type and workspace_id are force-new, and a replacement changes the connection
  # string both app_settings read and drops telemetry history. Fail the plan instead.
  lifecycle {
    prevent_destroy = true
  }
}

# Private path from the func subnet into App Insights via AMPLS.
# Without it the indexer's OTel exporters (Program.cs AddAzureMonitor*Exporter) reach
# westeurope-5.in.applicationinsights.azure.com over the public internet through the hub firewall
# (vnet_route_all_enabled + the 0.0.0.0/0 UDR) - and App Insights was receiving zero telemetry
# (confirmed 2026-08-07, 48h query window).
#   - Public ingestion/query is left enabled on every resource here; this only adds a second,
#     private path via longest-prefix-match routing, so portal and ad-hoc az queries keep working.
#   - The scope stays in the platform team's ai RG, where it was originally created - moving it to
#     the api RG would destroy and recreate the scope and its private endpoint for no gain.
#   - See docs/2608/260807/app-insights-private-link.md.

resource "azurerm_monitor_private_link_scope" "main" {
  name                = "con-ampls-cap-${local.env}-${local.region}-${local.instance}"
  resource_group_name = data.azurerm_resource_group.ai.name

  tags = local.common_tags
}

# Links App Insights into the scope - required before telemetry can flow over the endpoint below.
# Repointed from the landing-zone component to ours, which replaces this scoped service (cheap: it
# is just a link, no data).
resource "azurerm_monitor_private_link_scoped_service" "app_insights" {
  name                = "con-ampls-svc-appi-cap-${local.env}-${local.region}-${local.instance}"
  resource_group_name = data.azurerm_resource_group.ai.name
  scope_name          = azurerm_monitor_private_link_scope.main.name
  linked_resource_id  = azurerm_application_insights.main.id
}

# The backing workspace linked too, per Microsoft's AMPLS guidance - now possible, and the reason
# it was dropped is gone. It was previously skipped because the workspace lived in a subscription
# the deploy SP cannot read at all (providers.tf's azurerm.logmgmt alias); the workspace above is
# in our own RG.
resource "azurerm_monitor_private_link_scoped_service" "log_analytics" {
  name                = "con-ampls-svc-log-cap-${local.env}-${local.region}-${local.instance}"
  resource_group_name = data.azurerm_resource_group.ai.name
  scope_name          = azurerm_monitor_private_link_scope.main.name
  linked_resource_id  = azurerm_log_analytics_workspace.main.id
}

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
