# Two storage accounts in the landing-zone data RG, both private-endpoint-only.
#   - func: AzureWebJobsStorage + Durable task hub state (blob, queue, table) and the EP1 content
#     share (Azure Files, key-based - no managed-identity auth path; see function_app.tf). Pipeline
#     checkpoint state also lives here (azurerm_storage_container.indexing_pipeline).
#   - data: source documents, chunks and reports (blob only, one container per purpose).
#   - Private endpoints attach their private_dns_zone_group directly rather than waiting on the
#     platform team's policy-based zone linking
#     (docs/2607/260720/platform-team-dns-verzoek.md). Intra-VNet traffic wins over the spoke's
#     0.0.0.0/0 firewall UDR by longest-prefix match, so no override.
#   - The two accounts stay explicit rather than looped: their policies differ (versioning, dev IP
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

# Containers on the data account, looped - all private, they differ only by name.
#   - documents: source PDFs.
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
locals {
  data_containers = toset([
    "documents",
    "pipeline-reports",
    "pipeline-artifacts",
    "test-questions",
    "eval-results",
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
# so func needs one per service (blob/queue/table/file); data needs blob only. The key is the
# <target> segment of the endpoint name (naming.tf), so names are unchanged from the pre-loop
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
