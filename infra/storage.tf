# Three storage accounts in the landing-zone data RG, all private-endpoint-only.
#   - func: AzureWebJobsStorage + Durable task hub state (blob, queue, table) and the EP1 content
#     share (Azure Files, key-based - no managed-identity auth path; see function_app.tf). Pipeline
#     checkpoint state also lives here (azurerm_storage_container.indexing_pipeline).
#   - docs: the source corpus only - "documents" and "zenya-documents". Split off the data account
#     on 2026-09-21 (D206). The Zenya sync runs from an ADO hosted agent, which has no VNet path
#     here, so zenya-document-sync.yml has to open this account's firewall around each run; storage
#     network rules are account-wide (there is no per-container firewall), so while that window is
#     open EVERY container on the account is reachable from the runner. Keeping the corpus on its
#     own account makes that window's blast radius a corpus we can rebuild - re-uploadable via
#     5-upload-sample-pdfs.yml, re-syncable from Zenya - instead of reaching the vector cache and
#     every report and eval we cannot.
#   - data: reports, artifacts and eval history (blob only, one container per purpose). Nothing on
#     this account is reachable from an ADO runner any more; only the app's own identities and the
#     dev IPs below touch it.
#   - Private endpoints attach their private_dns_zone_group directly rather than waiting on the
#     platform team's policy-based zone linking
#     (docs/2607/260720/platform-team-dns-verzoek.md). Intra-VNet traffic wins over the spoke's
#     0.0.0.0/0 firewall UDR by longest-prefix match, so no override.
#   - The accounts stay explicit rather than looped: their policies differ (versioning, dev IP
#     access) - the same call network.tf makes for its NSGs.

resource "azurerm_storage_account" "func" {
  name                     = lower("constfunccap${local.env}${local.region}")
  resource_group_name      = data.azurerm_resource_group.data.name
  location                 = var.location
  account_tier             = "Standard"
  account_replication_type = "ZRS"
  account_kind             = "StorageV2"
  min_tls_version          = "TLS1_2"

  public_network_access_enabled   = false
  allow_nested_items_to_be_public = false
  # shared_access_key_enabled stays at its default (true): WEBSITE_CONTENTAZUREFILECONNECTIONSTRING
  # needs a key, and the toggle is account-wide - so Durable state and indexing-pipeline are
  # key-accessible too, not RBAC-only. Fix: move the content share to its own account and disable
  # keys here. Deferred.

  blob_properties {
    versioning_enabled = true
    delete_retention_policy {
      days = 7
    }
    container_delete_retention_policy {
      days = 7
    }
  }

  tags = local.common_tags
}

# Source corpus account (2026-09-21, D206). Same shape as the data account below; it differs only
# in what the Zenya sync is allowed to do to it - see zenya_sync_identity.tf, which scopes that
# identity's Storage Account Contributor (the role that carries the firewall write, and listKeys)
# to this account alone.
resource "azurerm_storage_account" "docs" {
  name                     = lower("constdocscap${local.env}${local.region}")
  resource_group_name      = data.azurerm_resource_group.data.name
  location                 = var.location
  account_tier             = "Standard"
  account_replication_type = "ZRS"
  account_kind             = "StorageV2"
  min_tls_version          = "TLS1_2"

  # Enabled in both environments, unlike the data account: zenya-document-sync.yml has to reach
  # this account from an ADO hosted agent in prod too, and flipping publicNetworkAccess
  # Disabled->Enabled per run costs minutes of propagation (measured in upload-sample-pdfs.yml,
  # and it is what the 2026-09-11 run sat in). network_rules below still denies by default; the
  # sync widens defaultAction to Allow for the length of one run and restores Deny after.
  public_network_access_enabled   = true
  allow_nested_items_to_be_public = false
  # shared_access_key_enabled stays at its default (true) for the same reason as the data account:
  # disabling it needs the deployer to hold Storage Blob Data Contributor before Terraform can
  # manage containers via storage_use_azuread - an RBAC-propagation race on a fresh apply. It
  # matters more here than there, because this is the account the sync opens to the internet:
  # while that window is open a leaked account key would be usable from anywhere. Revisit first.
  network_rules {
    default_action = "Deny"
    bypass         = ["AzureServices"]
    ip_rules       = local.dev_direct_access_ips
  }

  blob_properties {
    delete_retention_policy {
      days = 7
    }
    container_delete_retention_policy {
      days = 7
    }
  }

  tags = local.common_tags
}

resource "azurerm_storage_account" "data" {
  name                     = lower("constdatacap${local.env}${local.region}")
  resource_group_name      = data.azurerm_resource_group.data.name
  location                 = var.location
  account_tier             = "Standard"
  account_replication_type = "ZRS"
  account_kind             = "StorageV2"
  min_tls_version          = "TLS1_2"

  # Public access only while dev_allowed_ips has entries (development); network_rules below still
  # denies everything except those IPs.
  public_network_access_enabled   = length(local.dev_direct_access_ips) > 0 ? true : false
  allow_nested_items_to_be_public = false
  # shared_access_key_enabled stays at its default (true): disabling it needs the deployer to hold
  # Storage Blob Data Contributor before Terraform can manage containers via storage_use_azuread -
  # an RBAC-propagation race on a fresh apply. Revisit once that grant exists.

  dynamic "network_rules" {
    for_each = length(local.dev_direct_access_ips) > 0 ? [1] : []
    content {
      default_action = "Deny"
      bypass         = ["AzureServices"]
      ip_rules       = local.dev_direct_access_ips
    }
  }

  blob_properties {
    delete_retention_policy {
      days = 7
    }
    container_delete_retention_policy {
      days = 7
    }
  }

  tags = local.common_tags
}

# Containers on the docs account, looped - all private, they differ only by name.
# Which of the two the indexer reads is STORAGE_CONTAINER (function_app.tf), not a literal in code -
# see the "source-documents" keyed registration in Infrastructure/Clients/ServiceCollectionExtensions.cs.
#   - zenya-documents: Zenya sync target (D185): pdf/ and docs/ prefixes, written only by the
#     ZenyaSync tool. THE INDEXED CORPUS since 2026-09-21 (D202 §6 decision 1, D206).
#   - documents: the hand-uploaded sample PDFs (5-upload-sample-pdfs.yml). Was the indexed corpus
#     until 2026-09-21; kept as the test corpus, and indexed again only by pointing
#     STORAGE_CONTAINER back at it. Not deleted: it is the fallback if the Zenya corpus turns out
#     wrong, and re-uploading it is a pipeline run rather than a recovery.
locals {
  docs_containers = toset([
    "documents",
    "zenya-documents",
  ])
}

resource "azurerm_storage_container" "docs" {
  for_each              = local.docs_containers
  name                  = each.key
  storage_account_id    = azurerm_storage_account.docs.id
  container_access_type = "private"
}

# Containers on the data account, looped - all private, they differ only by name.
#   - pipeline-reports: every report - IRunReportWriter (gated on IsDevelopment), per-stage
#     artifact archives, corpus snapshots, eval results (see Observability/Reports.md). The name
#     must match Program.cs's GetBlobContainerClient("pipeline-reports"); it was once
#     "telemetry-reports" here, which left this container empty while writes went to an unmanaged
#     auto-created one.
#   - pipeline-artifacts: VectorCache only (vector-cache/ prefix). Existed in Azure by runtime
#     auto-creation before it was declared; the import block below adopts it. NEVER resolve a
#     conflict by destroy/recreate - that loses every cached embedding and forces a full re-embed.
#   - test-questions: golden questions.
#   - eval-results: no longer written to (eval-publish-results.yml uploads into pipeline-reports);
#     kept because it holds every eval run's history from before that change.
#   - documents, zenya-documents: STILL HERE ON PURPOSE, and only until the cutover finishes.
#     The corpus's new home is azurerm_storage_container.docs above, on the docs account; these two
#     are the old copies. Deleting them from this set is what eventually removes them, and that is
#     deliberately a SEPARATE change (see the step-6 note below and D206 §6) - dropping them in the
#     same apply that creates the new account would destroy the live corpus, blobs included, before
#     anything had been copied to replace it. So both copies exist side by side for one cutover, the
#     app is repointed, a full index run is verified, and only then does a follow-up change delete
#     these two entries and let terraform destroy them.
#
#     A `removed` block with `lifecycle { destroy = false }` would have expressed "forget without
#     deleting" directly, and was tried first - terraform rejects it here, because `removed.from`
#     takes a resource address and not a resource INSTANCE address, so it cannot name single
#     for_each keys ("Resource instance keys not allowed", init of 2026-09-21). The equivalent for
#     one key is an out-of-band `terraform state rm 'azurerm_storage_container.data["documents"]'`,
#     which this config deliberately does not depend on: it is a manual step against remote state,
#     and the pipeline is the only thing that touches state.
locals {
  data_containers = toset([
    "pipeline-reports",
    "pipeline-artifacts",
    "test-questions",
    "eval-results",
    # Remove these two only after D206 §6 step 4 (a full index run verified against the docs
    # account). The apply that removes them will plan "2 to destroy" - that is the intended
    # cleanup at that point, and nothing should be reading them any more.
    "documents",
    "zenya-documents",
  ])
}

resource "azurerm_storage_container" "data" {
  for_each              = local.data_containers
  name                  = each.key
  storage_account_id    = azurerm_storage_account.data.id
  container_access_type = "private"
}

# ARM ID form (azurerm v4), not the https://<account>.blob.core.windows.net/<name> URL - the URL
# form fails to import against this provider version. No-op once the container is in state.
import {
  to = azurerm_storage_container.data["pipeline-artifacts"]
  id = "${azurerm_storage_account.data.id}/blobServices/default/containers/pipeline-artifacts"
}

# Private endpoints, looped. Storage permits one group ID per endpoint ("OnlyOneGroupIdPermitted"),
# so func needs one per service (blob/queue/table/file); docs and data need blob only. The key is
# the <target> segment of the endpoint name (naming.tf), so names are unchanged from the pre-loop
# resources.
locals {
  storage_private_endpoints = {
    "stfunc-blob" = {
      target      = azurerm_storage_account.func.id
      subresource = "blob"
      dns_zone    = data.azurerm_private_dns_zone.blob.id
    }
    "stfunc-queue" = {
      target      = azurerm_storage_account.func.id
      subresource = "queue"
      dns_zone    = data.azurerm_private_dns_zone.queue.id
    }
    "stfunc-table" = {
      target      = azurerm_storage_account.func.id
      subresource = "table"
      dns_zone    = data.azurerm_private_dns_zone.table.id
    }
    "stfunc-file" = {
      target      = azurerm_storage_account.func.id
      subresource = "file"
      dns_zone    = data.azurerm_private_dns_zone.file.id
    }
    # The Function App reads the corpus over this endpoint, never over the public path the Zenya
    # sync opens - Track B (the sync moving into the Function App) needs no firewall window at all.
    "stdocs" = {
      target      = azurerm_storage_account.docs.id
      subresource = "blob"
      dns_zone    = data.azurerm_private_dns_zone.blob.id
    }
    "stdata" = {
      target      = azurerm_storage_account.data.id
      subresource = "blob"
      dns_zone    = data.azurerm_private_dns_zone.blob.id
    }
  }
}

resource "azurerm_private_endpoint" "storage" {
  for_each                      = local.storage_private_endpoints
  name                          = "con-pep-${each.key}-cap-${local.env}-${local.region}-${local.instance}"
  location                      = var.location
  resource_group_name           = data.azurerm_resource_group.data.name
  subnet_id                     = data.azurerm_subnet.pe.id
  custom_network_interface_name = "con-pep-${each.key}-cap-${local.env}-${local.region}-${local.instance}_nic"

  private_service_connection {
    name                           = "con-pep-${each.key}-cap-${local.env}-${local.region}-${local.instance}-psc"
    private_connection_resource_id = each.value.target
    subresource_names              = [each.value.subresource]
    is_manual_connection           = false
  }

  private_dns_zone_group {
    name                 = "default"
    private_dns_zone_ids = [each.value.dns_zone]
  }

  tags = local.common_tags
}
