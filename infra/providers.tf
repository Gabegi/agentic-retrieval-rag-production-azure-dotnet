terraform {
  required_version = ">= 1.15.7"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
    # ARM REST passthrough - only used in search.tf to approve the search
    # service's shared private link connection to the Foundry account.
    # azurerm has no resource for Cognitive Services private endpoint
    # connections (unlike Storage/Key Vault), so that one step can't be
    # done with azurerm alone.
    azapi = {
      source  = "Azure/azapi"
      version = "~> 2.0"
    }
    # Only used in search.tf to give Azure time to materialize the
    # shared-private-link connection object on the Foundry account before
    # azapi looks it up - see the comment there.
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

# Hub/connectivity subscription - owns the central
# private DNS zones our private endpoints need to resolve against
# (docs/platform-team-dns-verzoek.md). Read-only use only (data sources) -
# this repo doesn't manage anything in that subscription. Same OIDC identity
# as the default provider; it just needs at least Reader there, confirmed
# via the diagnostic step in 1-infra-deploy.yml.
provider "azurerm" {
  alias           = "hub"
  subscription_id = "00000000-0000-0000-0000-000000000000" # hub/connectivity subscription
  features {}

  # This alias itself is only ever used for data sources (see data.tf) - no
  # resource is provisioned through it directly, so it never needs to
  # register resource providers in the hub subscription. The SP does also
  # have write access there now (Private DNS Zone Contributor, confirmed by
  # the platform team), which is what lets the private_dns_zone_group blocks
  # on our private endpoints create their A records in the hub zones - that
  # write happens implicitly via ARM when the zone group is created, not
  # through this provider alias.
  resource_provider_registrations = "none"
}

# Log Analytics/management subscription - owns the
# workspace App Insights writes into (IngestionMode: LogAnalytics on
# con-appi-cap-*, see data.azurerm_application_insights.main.WorkspaceResourceId).
# A THIRD subscription, distinct from both the default provider and the hub
# alias above.
#
# CURRENTLY UNUSED - confirmed 2026-08-07 via a real `terraform apply` that
# this SP has zero access here: even provider initialization itself
# (Microsoft.Resources/subscriptions/providers/read, evaluated at the
# subscription scope) 403s, before getting anywhere near an actual resource
# read. app_insights_privatelink.tf's Log Analytics scoped-service link was
# dropped rather than blocked on this - see that file's comment. Left
# declared (not deleted) for when the platform team grants this SP Reader at
# the subscription scope - see docs/2608/260807/app-insights-private-link.md.
# The pipeline's 'VERIFY: App Insights private-link DNS zones + Log
# Analytics access' step still checks for that grant on every Plan run, so
# re-adding the data source + scoped-service link is a signal away, not a
# guess.
provider "azurerm" {
  alias           = "logmgmt"
  subscription_id = "00000000-0000-0000-0000-000000000000" # log analytics/management subscription
  features {}

  resource_provider_registrations = "none"
}
