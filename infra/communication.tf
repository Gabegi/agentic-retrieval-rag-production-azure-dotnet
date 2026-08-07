# ---------------------------------------------------------------------------
# Azure Communication Services - email delivery for the pipeline run report
# (SendReportEmailActivity). One email per indexing/restore run.
#
# See docs/2608/260807/pipeline-run-email-report.md for the delivery design and
# pipeline-email-report-structure.md for what the mail contains.
# ---------------------------------------------------------------------------

# The azurerm v4 provider (providers.tf's plain `features {}`, i.e. the default
# resource_provider_registrations = "core") only auto-registers a curated list
# of common resource providers. Microsoft.Communication is not on that list, so
# without this the first apply fails with MissingSubscriptionRegistration - this
# subscription had never had an ACS resource on it before. Explicit registration
# resource rather than a manual `az provider register` so the fix is captured in
# state and doesn't depend on someone running an out-of-band command once.
resource "azurerm_resource_provider_registration" "communication" {
  name = "Microsoft.Communication"
}

resource "azurerm_communication_service" "main" {
  name                = "cor-acs-cap-${local.env}-${local.region}-${local.instance}"
  resource_group_name = data.azurerm_resource_group.data.name
  # ACS is a global service - it has no regional deployment, and data_location
  # controls where data is stored at rest, not where the resource "lives".
  data_location = "Europe"

  tags = local.common_tags

  depends_on = [azurerm_resource_provider_registration.communication]
}

resource "azurerm_email_communication_service" "main" {
  name                = "cor-acsemail-cap-${local.env}-${local.region}-${local.instance}"
  resource_group_name = data.azurerm_resource_group.data.name
  data_location       = "Europe"

  tags = local.common_tags

  depends_on = [azurerm_resource_provider_registration.communication]
}

# Azure-managed subdomain (donotreply@<guid>.azurecomm.net) rather than a custom
# Contoso domain.
#
# The sender address carries no meaning here - this is an internal pipeline
# notification - and the managed subdomain provisions instantly with zero DNS
# work. A custom domain would need DKIM/SPF records published by whoever
# administers that zone, and this repo already carries one platform-team DNS
# dependency (docs/platform-team-dns-verzoek.md); a second one would block the
# whole feature on someone else's queue.
#
# Trade-off accepted: azurecomm.net senders are more likely to be spam-filtered,
# so recipients should allowlist the sender on first delivery. Switching to a
# custom domain later is a config change (domain_management + the DNS records),
# not a redesign.
resource "azurerm_email_communication_service_domain" "managed" {
  name              = "AzureManagedDomain"
  email_service_id  = azurerm_email_communication_service.main.id
  domain_management = "AzureManaged"

  tags = local.common_tags
}

resource "azurerm_communication_service_email_domain_association" "main" {
  communication_service_id = azurerm_communication_service.main.id
  email_service_domain_id  = azurerm_email_communication_service_domain.managed.id
}

# Assigned directly to the function's system-assigned managed identity, NOT via
# an Entra group. Managed identities are a distinct principal type and
# group-based role assignment does not resolve reliably for them - the failure
# mode is an authorization error that looks like a transient 403 and costs a lot
# of debugging time before anyone suspects the group.
#
# "Contributor" would also work and is what most samples reach for; this is the
# least-privilege equivalent for the managed-identity email path.
resource "azurerm_role_assignment" "func_acs_email_sender" {
  scope                = azurerm_communication_service.main.id
  role_definition_name = "Communication and Email Service Owner"
  principal_id         = azurerm_windows_function_app.indexer.identity[0].principal_id
}
