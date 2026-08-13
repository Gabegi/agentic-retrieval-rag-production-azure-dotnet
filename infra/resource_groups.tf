# ---------------------------------------------------------------------------
# Resource groups owned/created by this Terraform config (as opposed to the
# network/ai/data RGs in data.tf, which are owned by the platform team).
# ---------------------------------------------------------------------------

# Hosts the query API App Service (app_service.tf).
resource "azurerm_resource_group" "api" {
  name     = "con-cap-api-${local.env}-${local.region}-${local.instance}"
  location = var.location
  tags     = local.common_tags
}
