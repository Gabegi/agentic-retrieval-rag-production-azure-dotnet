# ---------------------------------------------------------------------------
# Two storage accounts in the existing data RG:
#   - func: AzureWebJobsStorage + Durable Functions task hub state for the
#     indexing Function App (needs blob, queue, and table), plus the file
#     share used for the Function App's WEBSITE_CONTENTAZUREFILECONNECTIONSTRING
#     content share (Elastic Premium always needs one; Azure Files/SMB has no
#     managed-identity auth, so this piece stays key-based - see
#     function_app.tf).
#   - data: source documents, chunks, and reports for the indexing/query
#     pipeline (blob only, organized by container). Pipeline checkpoint
#     state lives in the func account's indexing-pipeline container instead
#     (see azurerm_storage_container.indexing_pipeline, function_app.tf).
# Both are private-endpoint-only (no public network access). Each private
# endpoint below attaches its private_dns_zone_group directly rather than
# waiting on the platform team's policy-based zone linking
# (docs/platform-team-dns-verzoek.md). Their traffic stays VNet-local without
# any override: Azure's default system route for the VNet's own address space
# is a longer, more specific prefix than the spoke route table's sole
# 0.0.0.0/0 -> firewall UDR, so under longest-prefix-match routing, intra-VNet
# communication (this subnet <-> the pe subnet) takes precedence over that UDR.
# ---------------------------------------------------------------------------

resource "azurerm_storage_account" "func" {
  name                     = lower("corstfunccap${local.env}${local.region}")
  resource_group_name      = data.azurerm_resource_group.data.name
  location                 = var.location
  account_tier             = "Standard"
  account_replication_type = "ZRS"
  account_kind             = "StorageV2"
  min_tls_version          = "TLS1_2"

  public_network_access_enabled   = false
  allow_nested_items_to_be_public = false
  # shared_access_key_enabled left at its default (true), unlike the "data"
  # account: WEBSITE_CONTENTAZUREFILECONNECTIONSTRING (function_app.tf) needs
  # a key-based connection string for the Content Share, and this is an
  # account-wide toggle - so the indexing-pipeline blob container and the
  # Durable task hub state end up key-accessible too, not just RBAC-only via
  # the indexer's managed identity. Fix would be to move the Content Share
  # onto its own dedicated storage account and set shared_access_key_enabled
  # = false here. Deferred - revisit later.

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
  name                     = lower("corstdatacap${local.env}${local.region}")
  resource_group_name      = data.azurerm_resource_group.data.name
  location                 = var.location
  account_tier             = "Standard"
  account_replication_type = "ZRS"
  account_kind             = "StorageV2"
  min_tls_version          = "TLS1_2"

  # Only flips to true when dev_allowed_ips has entries (development only, per
  # variables.tf's dev_allowed_ips) - the network_rules block below still denies
  # everything except those specific IPs, so this isn't a general public-access opt-in.
  public_network_access_enabled   = length(local.dev_direct_access_ips) > 0 ? true : false
  allow_nested_items_to_be_public = false
  # shared_access_key_enabled left at its default (true): disabling it would
  # require the deploying identity to have Storage Blob Data Contributor
  # (data-plane RBAC, separate from Contributor) before Terraform can manage
  # containers via storage_use_azuread, which risks an RBAC-propagation race
  # on a fresh apply. Revisit once that identity's data-plane access is set up.

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

resource "azurerm_storage_container" "documents" {
  name                  = "documents"
  storage_account_id    = azurerm_storage_account.data.id
  container_access_type = "private"
}

# Written by IRunReportWriter (Observability/RunReportWriter.cs) - gated on
# IsEnabled (env.IsDevelopment()), so this stays empty unless DOTNET_ENVIRONMENT
# is set to Development on the function app. Name must match the container name
# RunReportWriter's registration uses (Program.cs, GetBlobContainerClient("pipeline-reports"));
# previously named "telemetry-reports" here, which didn't match and left this
# container permanently empty while writes went to an unmanaged, auto-created
# "pipeline-reports" container instead.
resource "azurerm_storage_container" "pipeline_reports" {
  name                  = "pipeline-reports"
  storage_account_id    = azurerm_storage_account.data.id
  container_access_type = "private"
}

resource "azurerm_storage_container" "test_questions" {
  name                  = "test-questions"
  storage_account_id    = azurerm_storage_account.data.id
  container_access_type = "private"
}

# Written by EvalResultWriter (RagApp.Evaluation.Tests) during the Evaluate pipeline
# stage - one JSONL blob per eval run. Previously left unmanaged on the assumption
# the writer would lazily create it (see eval_access.tf), but it never did, so every
# eval run 404'd with ContainerNotFound.
resource "azurerm_storage_container" "eval_results" {
  name                  = "eval-results"
  storage_account_id    = azurerm_storage_account.data.id
  container_access_type = "private"
}

# Azure Storage only permits one group Id per private endpoint for this
# account ("OnlyOneGroupIdPermitted... first-party resource"), so blob/queue/
# table each need their own private endpoint rather than one bundled PE.
resource "azurerm_private_endpoint" "stfunc_blob" {
  name                          = "cor-pep-stfunc-blob-cap-${local.env}-${local.region}-${local.instance}"
  location                      = var.location
  resource_group_name           = data.azurerm_resource_group.data.name
  subnet_id                     = data.azurerm_subnet.pe.id
  custom_network_interface_name = "cor-pep-stfunc-blob-cap-${local.env}-${local.region}-${local.instance}_nic"

  private_service_connection {
    name                           = "cor-pep-stfunc-blob-cap-${local.env}-${local.region}-${local.instance}-psc"
    private_connection_resource_id = azurerm_storage_account.func.id
    subresource_names              = ["blob"]
    is_manual_connection           = false
  }

  private_dns_zone_group {
    name                 = "default"
    private_dns_zone_ids = [data.azurerm_private_dns_zone.blob.id]
  }

  tags = local.common_tags
}

resource "azurerm_private_endpoint" "stfunc_queue" {
  name                          = "cor-pep-stfunc-queue-cap-${local.env}-${local.region}-${local.instance}"
  location                      = var.location
  resource_group_name           = data.azurerm_resource_group.data.name
  subnet_id                     = data.azurerm_subnet.pe.id
  custom_network_interface_name = "cor-pep-stfunc-queue-cap-${local.env}-${local.region}-${local.instance}_nic"

  private_service_connection {
    name                           = "cor-pep-stfunc-queue-cap-${local.env}-${local.region}-${local.instance}-psc"
    private_connection_resource_id = azurerm_storage_account.func.id
    subresource_names              = ["queue"]
    is_manual_connection           = false
  }

  private_dns_zone_group {
    name                 = "default"
    private_dns_zone_ids = [data.azurerm_private_dns_zone.queue.id]
  }

  tags = local.common_tags
}

resource "azurerm_private_endpoint" "stfunc_table" {
  name                          = "cor-pep-stfunc-table-cap-${local.env}-${local.region}-${local.instance}"
  location                      = var.location
  resource_group_name           = data.azurerm_resource_group.data.name
  subnet_id                     = data.azurerm_subnet.pe.id
  custom_network_interface_name = "cor-pep-stfunc-table-cap-${local.env}-${local.region}-${local.instance}_nic"

  private_service_connection {
    name                           = "cor-pep-stfunc-table-cap-${local.env}-${local.region}-${local.instance}-psc"
    private_connection_resource_id = azurerm_storage_account.func.id
    subresource_names              = ["table"]
    is_manual_connection           = false
  }

  private_dns_zone_group {
    name                 = "default"
    private_dns_zone_ids = [data.azurerm_private_dns_zone.table.id]
  }

  tags = local.common_tags
}

resource "azurerm_private_endpoint" "stfunc_file" {
  name                          = "cor-pep-stfunc-file-cap-${local.env}-${local.region}-${local.instance}"
  location                      = var.location
  resource_group_name           = data.azurerm_resource_group.data.name
  subnet_id                     = data.azurerm_subnet.pe.id
  custom_network_interface_name = "cor-pep-stfunc-file-cap-${local.env}-${local.region}-${local.instance}_nic"

  private_service_connection {
    name                           = "cor-pep-stfunc-file-cap-${local.env}-${local.region}-${local.instance}-psc"
    private_connection_resource_id = azurerm_storage_account.func.id
    subresource_names              = ["file"]
    is_manual_connection           = false
  }

  private_dns_zone_group {
    name                 = "default"
    private_dns_zone_ids = [data.azurerm_private_dns_zone.file.id]
  }

  tags = local.common_tags
}

resource "azurerm_private_endpoint" "stdata" {
  name                          = "cor-pep-stdata-cap-${local.env}-${local.region}-${local.instance}"
  location                      = var.location
  resource_group_name           = data.azurerm_resource_group.data.name
  subnet_id                     = data.azurerm_subnet.pe.id
  custom_network_interface_name = "cor-pep-stdata-cap-${local.env}-${local.region}-${local.instance}_nic"

  private_service_connection {
    name                           = "cor-pep-stdata-cap-${local.env}-${local.region}-${local.instance}-psc"
    private_connection_resource_id = azurerm_storage_account.data.id
    subresource_names              = ["blob"]
    is_manual_connection           = false
  }

  private_dns_zone_group {
    name                 = "default"
    private_dns_zone_ids = [data.azurerm_private_dns_zone.blob.id]
  }

  tags = local.common_tags
}
