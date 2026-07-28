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

# Hub/connectivity subscription (cor-connectivity-prd) - owns the central
# private DNS zones our private endpoints need to resolve against
# (docs/platform-team-dns-verzoek.md). Read-only use only (data sources) -
# this repo doesn't manage anything in that subscription. Same OIDC identity
# as the default provider; it just needs at least Reader there, confirmed
# via the diagnostic step in 1-infra-deploy.yml.
provider "azurerm" {
  alias           = "hub"
  subscription_id = "c8e46005-ce0e-4be5-9ded-0178e19fbe28" # cor-connectivity-prd
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
