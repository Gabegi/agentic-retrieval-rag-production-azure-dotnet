resource "azurerm_search_service" "main" {
  name                = "con-srch-cap-${local.env}-${local.region}-${local.instance}"
  location            = var.location
  resource_group_name = data.azurerm_resource_group.data.name
  # S3 - top of the Basic/Standard family that supports knowledge bases. Agentic retrieval leans on
  # the semantic ranker, whose concurrency scales with tier (S1: 3 concurrent + 6 queued per SU;
  # S2/S3: 4 + 8), so the tier caps knowledge-base throughput.
  #   - NOT S3 HD: same SKU with hosting_mode = "highDensity", which allows zero knowledge bases and
  #     zero knowledge sources - it would break agentic retrieval outright.
  #   - Upgrades within Basic/Standard apply in place; no destroy/recreate of the service or its
  #     indexes.
  sku = "standard3"

  # Semantic ranker - KnowledgeService.cs always requests semantic ranking; without it every query
  # fails with FeatureNotSupportedInService. "standard" is the top plan ("free" caps at
  # 1,000 queries/month).
  semantic_search_sku = "standard"

  # Public access only while dev_direct_access_ips has entries (development); allowed_ips still
  # restricts inbound to exactly those.
  public_network_access_enabled = length(local.dev_direct_access_ips) > 0 ? true : false
  allowed_ips                   = local.dev_direct_access_ips

  # The data plane defaults to apiKeyOnly. The indexer authenticates with an AAD bearer token, so
  # without this every call 403s regardless of the RBAC grants in function_app.tf - AAD isn't even
  # considered as a credential type.
  local_authentication_enabled = true
  authentication_failure_mode  = "http401WithBearerChallenge"

  identity {
    type = "SystemAssigned"
  }

  tags = local.common_tags
}

# Outbound shared private link so the search service itself can reach the Foundry account (public
# access disabled there) - the index's AzureOpenAIVectorizer (IndexService.cs) and the knowledge
# base's AzureOpenAIModel (KnowledgeService.cs) call it from Search, not from our apps. This is how
# Search reaches other PaaS, not how clients reach Search (that is the private endpoint below).
# Created "Pending"; approved by the azapi block below.
resource "azurerm_search_shared_private_link_service" "openai" {
  name               = "con-spl-srch-openai-cap-${local.env}-${local.region}-${local.instance}"
  search_service_id  = azurerm_search_service.main.id
  subresource_name   = "openai_account"
  target_resource_id = data.azurerm_cognitive_account.foundry.id
  request_message    = "Approve for search knowledge base / vectorizer access to Azure OpenAI"
}

# --- Auto-approve the shared private link -----------------------------------
# azurerm has no resource for Cognitive Services private endpoint connections (unlike Storage/Key
# Vault, where is_manual_connection = false auto-approves), so the approval goes through azapi.
#   - Azure creates the connection on the Foundry account asynchronously, with an auto-generated
#     name, so we wait rather than race it. Best-effort: if it isn't there within 90s the apply
#     fails to find a Pending entry and needs a re-run.
#   - The connection is matched by exclusion (Pending) on the first apply - Azure exposes no
#     back-reference to the requesting search service - and on every later run by the exact
#     description this resource wrote, since a Pending-only filter would then match nothing and
#     one(...) would return null. one(...) still errors if more than one matches.
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

# Search's own identity calls the vectorizer/knowledge-base model with no API key
# (IndexService.cs, KnowledgeService.cs). Account scope, not project - RBAC only inherits downward.
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
