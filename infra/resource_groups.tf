# Resource groups created by this configuration - the network/ai/data RGs in data.tf belong to the
# platform team.

# Hosts the query API App Service (app_service.tf).
resource "azurerm_resource_group" "api" {
  name     = "con-cap-api-${local.env}-${local.region}-${local.instance}"
  location = var.location
  tags     = local.common_tags
}
