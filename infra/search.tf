resource "azurerm_search_service" "main" {
  name                = "con-srch-cap-${local.env}-${local.region}-${local.instance}"
  location            = var.location
  resource_group_name = data.azurerm_resource_group.data.name
  sku                 = "standard"

  # Enables semantic ranker - required by the knowledge base / agentic
  # retrieval queries in KnowledgeService.cs, which always request semantic
  # ranking regardless of the index's own semantic configuration. Without
  # this, every query fails with "Semantic Search is not enabled for this
  # service" (FeatureNotSupportedInService). "standard" (not "free") to
  # match the standard search service tier - free caps at 1,000 queries/month.
  semantic_search_sku = "standard"

  # Only flips to true when local.dev_direct_access_ips has entries (development
  # only) - allowed_ips below still restricts inbound to just those IPs.
  public_network_access_enabled = length(local.dev_direct_access_ips) > 0 ? true : false
  # NOTE: argument name not verified against provider docs (offline at edit time) -
  # confirm this is correct via `terraform plan` before applying; if it errors,
  # check the azurerm_search_service docs for the actual IP-allowlist argument.
  allowed_ips = local.dev_direct_access_ips

  # Defaults to apiKeyOnly on the data-plane REST endpoint - the indexer's
  # managed identity authenticates with an AAD bearer token (DefaultAzureCredential),
  # so without this the service rejects every data-plane call with 403 regardless
  # of the RBAC roles granted below (search_index_contributor / search_service_contributor
  # in function_app.tf), since it isn't even considering AAD tokens as a credential type.
  local_authentication_enabled = true
  authentication_failure_mode  = "http401WithBearerChallenge"

  identity {
    type = "SystemAssigned"
  }

  tags = local.common_tags
}

# Outbound private connection so the search service itself can reach the
# Foundry/OpenAI account (public network access disabled there) - needed for
# the index's AzureOpenAIVectorizer (IndexService.cs) and the knowledge
# base's AzureOpenAIModel (KnowledgeService.cs), both of which call the
# account directly from the search service, not from our func/api apps.
# This is a *shared private link*, a different mechanism from the
# azurerm_private_endpoint below - it's how Search reaches other network-
# restricted PaaS resources, not how clients reach Search.
# Created in "Pending" status - see the azapi block below for how it gets
# approved.
resource "azurerm_search_shared_private_link_service" "openai" {
  name               = "con-spl-srch-openai-cap-${local.env}-${local.region}-${local.instance}"
  search_service_id  = azurerm_search_service.main.id
  subresource_name   = "openai_account"
  target_resource_id = data.azurerm_cognitive_account.foundry.id
  request_message    = "Approve for search knowledge base / vectorizer access to Azure OpenAI"
}

# --- Auto-approve the shared private link's connection ----------------------
# azurerm has no resource for Cognitive Services private endpoint
# connections (unlike Storage/Key Vault, where a private endpoint's
# is_manual_connection = false gets auto-approved by azurerm itself) - so
# approving the connection Azure creates on the Foundry account has to go
# through azapi, an ARM REST passthrough provider, instead.

# Azure creates the privateEndpointConnections child resource on the
# Foundry account asynchronously after the shared private link request
# lands, with an auto-generated name that isn't known in advance - this
# waits before looking it up rather than racing Azure's side effect.
# Best-effort: if Azure hasn't materialized the connection within 90s, the
# apply below will fail to find a Pending entry and needs a re-run.
resource "time_sleep" "wait_for_openai_shared_link_connection" {
  depends_on      = [azurerm_search_shared_private_link_service.openai]
  create_duration = "90s"
}

data "azapi_resource_list" "foundry_private_endpoint_connections" {
  type                   = "Microsoft.CognitiveServices/accounts/privateEndpointConnections@2025-06-01"
  parent_id              = data.azurerm_cognitive_account.foundry.id
  response_export_values = ["*"]

  depends_on = [time_sleep.wait_for_openai_shared_link_connection]
}

resource "azapi_update_resource" "approve_openai_shared_link" {
  type = "Microsoft.CognitiveServices/accounts/privateEndpointConnections@2025-06-01"

  # Picks out our shared private link's connection - identified by
  # exclusion (still Pending) on the first apply, since Azure doesn't
  # expose a back-reference to the requesting search resource on this
  # object. On every later plan/apply that connection is already
  # Approved, so it's re-matched by the exact description this resource
  # itself wrote below - otherwise the Pending-only filter finds zero
  # matches once approved and `one(...)` returns null instead of an id.
  # `one(...)` still deliberately errors if more than one connection
  # matches at apply time.
  resource_id = one([
    for conn in data.azapi_resource_list.foundry_private_endpoint_connections.output.value :
    conn.id
    if conn.properties.privateLinkServiceConnectionState.status == "Pending"
    || conn.properties.privateLinkServiceConnectionState.description == "Approved via Terraform for the search service's OpenAI shared private link"
  ])

  body = {
    properties = {
      privateLinkServiceConnectionState = {
        status      = "Approved"
        description = "Approved via Terraform for the search service's OpenAI shared private link"
      }
    }
  }
}

# Search's own system-assigned identity needs this to call the vectorizer/
# knowledge-base model - the AzureOpenAIVectorizerParameters built in
# IndexService.cs and KnowledgeService.cs set no API key, so Search
# authenticates with its managed identity. Scoped to the account (not the
# project) for the same reason as func/api's openai_user role assignments -
# RBAC doesn't inherit upward from a project sub-resource.
resource "azurerm_role_assignment" "search_openai_user" {
  scope                = data.azurerm_cognitive_account.foundry.id
  role_definition_name = "Cognitive Services OpenAI User"
  principal_id         = azurerm_search_service.main.identity[0].principal_id
}

resource "azurerm_private_endpoint" "search" {
  name                          = "con-pep-srch-cap-${local.env}-${local.region}-${local.instance}"
  location                      = var.location
  resource_group_name           = data.azurerm_resource_group.data.name
  subnet_id                     = data.azurerm_subnet.pe.id
  custom_network_interface_name = "con-pep-srch-cap-${local.env}-${local.region}-${local.instance}_nic"

  private_service_connection {
    name                           = "con-pep-srch-cap-${local.env}-${local.region}-${local.instance}-psc"
    private_connection_resource_id = azurerm_search_service.main.id
    subresource_names              = ["searchService"]
    is_manual_connection           = false
  }

  private_dns_zone_group {
    name                 = "default"
    private_dns_zone_ids = [data.azurerm_private_dns_zone.search.id]
  }

  tags = local.common_tags
}
