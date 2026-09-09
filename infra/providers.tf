terraform {
  required_version = ">= 1.15.7"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
    # ARM REST passthrough, used only in search.tf: azurerm has no resource for Cognitive Services
    # private endpoint connections, so approving the search service's shared private link to the
    # Foundry account needs azapi.
    azapi = {
      source  = "Azure/azapi"
      version = "~> 2.0"
    }
    # Used only in search.tf, to give Azure time to materialize that connection before azapi looks
    # it up.
    time = {
      source  = "hashicorp/time"
      version = "~> 0.11"
    }
  }

  backend "azurerm" {}
}

provider "azurerm" {
  features {}
}

provider "azapi" {}

# Hub/connectivity subscription, which owns the private DNS zones our
# endpoints resolve against (docs/2607/260720/platform-team-dns-verzoek.md). Data sources only.
#   - Same OIDC identity as the default provider; needs at least Reader there (confirmed via the
#     diagnostic step in base/deploy-azure-infrastructure.yml).
#   - No resources are provisioned through this alias, hence no provider registration. The SP does
#     hold Private DNS Zone Contributor there, which is what lets private_dns_zone_group blocks
#     write A records - that happens via ARM when the zone group is created, not through this alias.
provider "azurerm" {
  alias           = "hub"
  subscription_id = "00000000-0000-0000-0000-000000000000" # hub/connectivity subscription
  features {}

  resource_provider_registrations = "none"
}

# Log Analytics/management subscription - a third subscription, owning the
# workspace the landing-zone team's App Insights component writes into. UNUSED, and as of
# 2026-09-09 nothing is waiting on it: app_insights.tf creates this config's own workspace instead
# of reading theirs, so no resource or data source here targets this subscription any more.
#   - Confirmed 2026-08-07 by a real apply: the SP has zero access here - even provider
#     initialization (Microsoft.Resources/subscriptions/providers/read) 403s. That 403 is what
#     drove the decision to own the workspace (see app_insights.tf's header).
#   - Safe to delete along with the pipeline's 'VERIFY: App Insights private-link DNS zones + Log
#     Analytics access' step, which is already commented out. Kept only so the history in
#     docs/2608/260807/app-insights-private-link.md still resolves against something.
provider "azurerm" {
  alias           = "logmgmt"
  subscription_id = "00000000-0000-0000-0000-000000000000" # log analytics/management subscription
  features {}

  resource_provider_registrations = "none"
}
