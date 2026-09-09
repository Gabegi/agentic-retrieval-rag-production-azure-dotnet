# Linux App Service (.NET 10, P1v3) - the query API in front of Azure AI Search and the Foundry GPT
# deployment.

resource "azurerm_service_plan" "api" {
  name                = "con-plan-api-cap-${local.env}-${local.region}-${local.instance}"
  resource_group_name = azurerm_resource_group.api.name
  location            = var.location
  os_type             = "Linux"
  sku_name            = "P1v3"

  tags = local.common_tags
}

resource "azurerm_linux_web_app" "api" {
  name                          = "con-app-api-cap-${local.env}-${local.region}-${local.instance}"
  resource_group_name           = azurerm_resource_group.api.name
  location                      = var.location
  service_plan_id               = azurerm_service_plan.api.id
  virtual_network_subnet_id     = azurerm_subnet.workload["api"].id
  public_network_access_enabled = false
  https_only                    = true

  identity {
    type = "SystemAssigned"
  }

  site_config {
    application_stack {
      dotnet_version = "10.0"
    }
    always_on              = true
    vnet_route_all_enabled = true
  }

  app_settings = {
    "APPLICATIONINSIGHTS_CONNECTION_STRING" = azurerm_application_insights.main.connection_string
    "SEARCH_ENDPOINT"                       = "https://${azurerm_search_service.main.name}.search.windows.net"
    "OPENAI_ENDPOINT"                       = data.azurerm_cognitive_account.foundry.endpoint
    "OPENAI_GPT_DEPLOYMENT"                 = var.openai_gpt_deployment
    "OPENAI_GPT_MODEL_NAME"                 = var.openai_gpt_model_name
    "SEARCH_INDEX_NAME"                     = var.search_index_name
    "KNOWLEDGE_SOURCE_NAME"                 = var.knowledge_source_name
    "KNOWLEDGE_BASE_NAME"                   = var.knowledge_base_name
  }

  tags = local.common_tags
}

resource "azurerm_private_endpoint" "api" {
  name                          = "con-pep-api-cap-${local.env}-${local.region}-${local.instance}"
  location                      = var.location
  resource_group_name           = azurerm_resource_group.api.name
  subnet_id                     = data.azurerm_subnet.pe.id
  custom_network_interface_name = "con-pep-api-cap-${local.env}-${local.region}-${local.instance}_nic"

  private_service_connection {
    name                           = "con-pep-api-cap-${local.env}-${local.region}-${local.instance}-psc"
    private_connection_resource_id = azurerm_linux_web_app.api.id
    subresource_names              = ["sites"]
    is_manual_connection           = false
  }

  private_dns_zone_group {
    name                 = "default"
    private_dns_zone_ids = [data.azurerm_private_dns_zone.azurewebsites.id]
  }

  tags = local.common_tags
}

# Role assignments for the API's identity, looped like function_app.tf's. Read-only on Search and
# OpenAI inference only; no storage access until the API fetches raw documents rather than just
# querying the index. Foundry grant is account-scoped - RBAC only inherits downward.
locals {
  api_role_assignments = {
    search_index_reader = {
      scope = azurerm_search_service.main.id
      role  = "Search Index Data Reader"
    }
    openai_user = {
      scope = data.azurerm_cognitive_account.foundry.id
      role  = "Cognitive Services OpenAI User"
    }
  }
}

resource "azurerm_role_assignment" "api" {
  for_each             = local.api_role_assignments
  scope                = each.value.scope
  role_definition_name = each.value.role
  principal_id         = azurerm_linux_web_app.api.identity[0].principal_id
}
