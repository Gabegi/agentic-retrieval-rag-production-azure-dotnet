# ---------------------------------------------------------------------------
# Windows Function App (dotnet-isolated, EP1 Premium) - durable indexing
# pipeline. Reuses the storage account, App Insights, Search, and Foundry
# resources already defined elsewhere rather than provisioning its own.
# VNet-integrated into the shared app subnet (outbound) with a private
# endpoint (inbound), matching the hub-firewall-routed architecture.
# ---------------------------------------------------------------------------

resource "azurerm_service_plan" "func" {
  name                = "cor-plan-func-cap-${local.env}-${local.region}-${local.instance}"
  resource_group_name = data.azurerm_resource_group.data.name
  location            = var.location
  os_type             = "Windows"
  # Elastic Premium, not P1v3 - matches the earlier decision to run the
  # durable indexing pipeline on a Premium (Elastic) plan.
  sku_name = "EP1"

  tags = local.common_tags
}

resource "azurerm_windows_function_app" "indexer" {
  name                          = "cor-func-idx-cap-${local.env}-${local.region}-${local.instance}"
  resource_group_name           = data.azurerm_resource_group.data.name
  location                      = var.location
  service_plan_id               = azurerm_service_plan.func.id
  storage_account_name          = azurerm_storage_account.func.name
  storage_uses_managed_identity = true
  virtual_network_subnet_id     = azurerm_subnet.workload["func"].id
  # Deny-by-default public access (no ip_restriction/scm_ip_restriction rules
  # managed here), so the private endpoint stays the only stable path in.
  # The app-deploy pipeline runs on a Microsoft-hosted agent with no VNet
  # access, so it opens a scoped Allow rule on the SCM site for its own
  # runner IP via `az functionapp config access-restriction add`
  # immediately before the zip deploy, then removes it again immediately
  # after - see 4-deploy-application.yml.
  public_network_access_enabled = true
  # storage_uses_managed_identity only covers AzureWebJobsStorage/Durable
  # Functions (blob/queue/table). The EP1 plan's content share still needs a
  # key-based connection string - Azure Files/SMB has no managed-identity
  # auth path - plus WEBSITE_CONTENTOVERVNET so the platform reaches it via
  # the private endpoint (azurerm_private_endpoint.stfunc_file in storage.tf)
  # instead of the public endpoint.

  identity {
    type = "SystemAssigned"
  }

  site_config {
    application_stack {
      dotnet_version              = "v10.0"
      use_dotnet_isolated_runtime = true
    }
    always_on                         = true
    vnet_route_all_enabled            = true
    ip_restriction_default_action     = "Deny"
    scm_ip_restriction_default_action = "Deny"

    dynamic "ip_restriction" {
      for_each = var.environment == "development" ? var.dev_allowed_ips : []
      content {
        name       = "dev-direct-access-${replace(ip_restriction.value, ".", "-")}"
        ip_address = "${ip_restriction.value}/32"
        action     = "Allow"
        priority   = 100
      }
    }

    cors {
      allowed_origins = ["https://portal.azure.com"]
    }
  }

  app_settings = {
    "FUNCTIONS_WORKER_RUNTIME"              = "dotnet-isolated"
    "APPLICATIONINSIGHTS_CONNECTION_STRING" = data.azurerm_application_insights.main.connection_string
    # Drives IHostEnvironment.IsDevelopment() in Program.cs - gates dev-only diagnostics
    # (console exporters) and IRunReportWriter's pipeline-reports writes. Derived from
    # var.environment so it flips to "Production" automatically on a prod deploy rather
    # than needing a separate var kept in sync.
    #
    # Both variables set deliberately: the isolated-worker Functions host determines
    # IHostEnvironment.EnvironmentName from AZURE_FUNCTIONS_ENVIRONMENT specifically (its
    # own host-configuration step calls .UseEnvironment() off that value, which takes
    # precedence over the generic host's DOTNET_ENVIRONMENT handling once deployed) -
    # DOTNET_ENVIRONMENT alone was set first and confirmed NOT sufficient (pipeline-reports
    # stayed empty even after it was live and the app restarted).
    "DOTNET_ENVIRONMENT"          = var.environment == "development" ? "Development" : "Production"
    "AZURE_FUNCTIONS_ENVIRONMENT" = var.environment == "development" ? "Development" : "Production"
    # Durable Functions managed-identity auth - no connection string needed
    "AzureWebJobsStorage__accountName" = azurerm_storage_account.func.name
    "AzureWebJobsStorage__credential"  = "managedidentity"
    # Content share: key-based (see note above azurerm_windows_function_app.indexer)
    "WEBSITE_CONTENTOVERVNET"                  = "1"
    "WEBSITE_CONTENTAZUREFILECONNECTIONSTRING" = azurerm_storage_account.func.primary_connection_string
    "WEBSITE_CONTENTSHARE"                     = azurerm_storage_share.func_content.name
    # WEBSITE_CONTENTOVERVNET alone doesn't make the site's own DNS resolution
    # (used by Kudu to resolve the content share's *.file.core.windows.net)
    # honor the VNet-linked private DNS zone - that needs this resolver
    # explicitly, or it falls back to public DNS and hits the storage
    # account's public endpoint, which public_network_access_enabled = false
    # on azurerm_storage_account.func then rejects.
    "ProtocolsStorage__blobServiceUri" = azurerm_storage_account.data.primary_blob_endpoint
    "STORAGE_ACCOUNT_URL"              = azurerm_storage_account.data.primary_blob_endpoint
    "SEARCH_ENDPOINT"                  = "https://${azurerm_search_service.main.name}.search.windows.net"
    "OPENAI_ENDPOINT"                  = data.azurerm_cognitive_account.foundry.endpoint
    "OPENAI_EMBEDDING_DEPLOYMENT"      = var.openai_embedding_deployment
    "OPENAI_GPT_DEPLOYMENT"            = var.openai_gpt_deployment
    "OPENAI_GPT_MODEL_NAME"            = var.openai_gpt_model_name
    "OPENAI_EXTRACTION_DEPLOYMENT"     = var.openai_extraction_deployment
    # Same account/endpoint as OPENAI_ENDPOINT above (document_intelligence.tf) -
    # setting this is what flips DocumentIntelligenceExtractor from unregistered
    # to active in program.cs (config.DocumentIntelligenceEndpoint gate).
    "DOCUMENT_INTELLIGENCE_ENDPOINT" = data.azurerm_cognitive_account.foundry.endpoint
    # Same account/endpoint again - Content Safety (Prompt Shields) and AI Language
    # (PII detection) are both exposed on this one AIServices-kind multi-service
    # account, confirmed live via direct REST calls (2026-08-06): both
    # text:shieldPrompt and language/:analyze-text return "PermissionDenied" (RBAC),
    # not 404, so no separate Content Safety/Language resource is needed. RBAC is
    # already covered too - func_document_intelligence_user in
    # document_intelligence.tf grants "Cognitive Services User" on this same
    # account, whose dataActions is the wildcard Microsoft.CognitiveServices/*.
    "CONTENT_SAFETY_ENDPOINT" = data.azurerm_cognitive_account.foundry.endpoint
    "LANGUAGE_ENDPOINT"       = data.azurerm_cognitive_account.foundry.endpoint
    "SEARCH_INDEX_NAME"       = var.search_index_name
    "KNOWLEDGE_SOURCE_NAME"   = var.knowledge_source_name
    "KNOWLEDGE_BASE_NAME"     = var.knowledge_base_name

    # Windows-only app setting: makes TimerTrigger cron expressions (e.g. ScheduledIndexing's
    # daily 22:00 run) evaluate against Dutch wall-clock time instead of UTC, so the trigger
    # stays at 22:00 local time across the DST transition rather than drifting by an hour.
    "WEBSITE_TIME_ZONE" = "W. Europe Standard Time"

    # ---------------------------------------------------------------------
    # Pipeline run report email (SendReportEmailActivity)
    # ---------------------------------------------------------------------
    # No separate storage connection needed: this is a Durable activity called
    # directly by IndexingOrchestrator/RestoreOrchestrator, not a blob-triggered
    # function, so it reads through the same BlobServiceClient (STORAGE_ACCOUNT_URL
    # above) every other in-process client already uses. An earlier version of
    # this feature was Event-Grid/blob-triggered and needed its own cross-account
    # connection setting plus a reconciliation timer to detect it silently not
    # firing - both are gone now that the orchestrator calls the activity
    # directly. See docs/2608/260807/pipeline-run-email-report.md.

    "ReportEmail__Enabled"    = tostring(var.report_email_enabled)
    "ReportEmail__Recipients" = var.report_email_recipients
    # VERIFY ON FIRST PLAN. An Azure-managed domain's actual sender host is a
    # GUID subdomain Azure assigns at create time, so it can only come from an
    # exported attribute - but `mail_from_sender_domain` has NOT been confirmed
    # against the pinned provider (~> 4.0); terraform validate can't run here
    # (providers uninitialised, and providers.tf requires >= 1.15.7 against a
    # 1.14.8 CLI). If plan rejects this attribute, try `from_sender_domain`;
    # if neither exists, set var.report_email_sender_address explicitly after
    # the first apply and reference that instead. This is a loud plan-time
    # failure, not a silent runtime one.
    "ReportEmail__SenderAddress" = var.report_email_sender_address != "" ? var.report_email_sender_address : "DoNotReply@${azurerm_email_communication_service_domain.managed.mail_from_sender_domain}"
    "ReportEmail__AcsEndpoint"   = "https://${azurerm_communication_service.main.name}.communication.azure.com"

    # Report-only period: the metrics whose thresholds have no defensible
    # source yet (chunk coherence, band percentages, duplicate rate, cost
    # multiplier) render their observed value but raise no flag until this is
    # switched off. Shipping guessed thresholds teaches people to ignore the
    # flags on run one - see pipeline-email-report-structure.md, decision 2.
    "ReportEmail__CalibrationMode" = tostring(var.report_email_calibration_mode)
  }

  tags = local.common_tags

  # Azure auto-links App Insights whenever it sees APPLICATIONINSIGHTS_CONNECTION_STRING
  # in app_settings above - it injects the "hidden-link: /app-insights-resource-id" tag and
  # site_config.application_insights_connection_string on the live resource itself, neither
  # of which is declared here. Without this, every plan sees Azure's own auto-linking as
  # drift and proposes removing it, only for Azure to re-add it right after apply.
  lifecycle {
    ignore_changes = [
      tags["hidden-link: /app-insights-resource-id"],
      site_config[0].application_insights_connection_string,
    ]
  }
}

#  need this on any EP1 Function App
resource "azurerm_storage_share" "func_content" {
  name               = "cor-func-idx-cap-${local.env}-${local.region}-${local.instance}"
  storage_account_id = azurerm_storage_account.func.id
  quota              = 100
}

# Temporary blob storage for large Durable payloads (extracted docs + chunks
# between activities) - lives on the function's own storage, not the shared
# data storage account.
resource "azurerm_storage_container" "indexing_pipeline" {
  name                  = "indexing-pipeline"
  storage_account_id    = azurerm_storage_account.func.id
  container_access_type = "private"
}

resource "azurerm_private_endpoint" "func" {
  name                          = "cor-pep-func-cap-${local.env}-${local.region}-${local.instance}"
  location                      = var.location
  resource_group_name           = data.azurerm_resource_group.data.name
  subnet_id                     = data.azurerm_subnet.pe.id
  custom_network_interface_name = "cor-pep-func-cap-${local.env}-${local.region}-${local.instance}_nic"

  private_service_connection {
    name                           = "cor-pep-func-cap-${local.env}-${local.region}-${local.instance}-psc"
    private_connection_resource_id = azurerm_windows_function_app.indexer.id
    subresource_names              = ["sites"]
    is_manual_connection           = false
  }

  private_dns_zone_group {
    name                 = "default"
    private_dns_zone_ids = [data.azurerm_private_dns_zone.azurewebsites.id]
  }

  tags = local.common_tags
}

# Payloads here are intermediate/disposable - expire them rather than let
# them accumulate indefinitely on an account with no other cleanup.
resource "azurerm_storage_management_policy" "func" {
  storage_account_id = azurerm_storage_account.func.id

  rule {
    name    = "expire-indexing-pipeline"
    enabled = true

    filters {
      blob_types   = ["blockBlob"]
      prefix_match = ["indexing-pipeline/"]
    }

    actions {
      base_blob {
        delete_after_days_since_modification_greater_than = 7
      }
      version {
        delete_after_days_since_creation = 7
      }
    }
  }
}

# --- Role assignments -------------------------------------------------------
# All scoped to the same principal (the indexer's identity) and looped via
# for_each rather than one resource block each - only scope/role vary.

locals {
  func_role_assignments = {
    # Account-wide (not container-scoped): required for AzureWebJobsStorage /
    # Durable Functions task hub state (storage_uses_managed_identity = true
    # above) - the Functions host creates and manages its own internal
    # containers at runtime, and Microsoft's identity-based-connection docs
    # specify Storage Blob Data Owner at the account level for this, not a
    # narrower scope. indexing_pipeline_contributor below is already covered
    # by this grant; it's additive, not a reduction, and only meaningful if
    # this one is ever narrowed.
    storage_owner = {
      scope = azurerm_storage_account.func.id
      role  = "Storage Blob Data Owner"
    }
    indexing_pipeline_contributor = {
      scope = azurerm_storage_container.indexing_pipeline.id
      role  = "Storage Blob Data Contributor"
    }
    # Durable Functions store orchestration state in queues and tables
    storage_queue_contributor = {
      scope = azurerm_storage_account.func.id
      role  = "Storage Queue Data Contributor"
    }
    storage_table_contributor = {
      scope = azurerm_storage_account.func.id
      role  = "Storage Table Data Contributor"
    }
    # Reads source documents, writes chunks/reports/state back to the data
    # storage account.
    data_storage_contributor = {
      scope = azurerm_storage_account.data.id
      role  = "Storage Blob Data Contributor"
    }
    search_index_contributor = {
      scope = azurerm_search_service.main.id
      role  = "Search Index Data Contributor"
    }
    search_service_contributor = {
      scope = azurerm_search_service.main.id
      role  = "Search Service Contributor"
    }
    # Scoped to the account, not the project: AzureOpenAIClient calls the
    # account's own endpoint directly (config.OpenAiEndpoint =
    # data.azurerm_cognitive_account.foundry.endpoint), with no project
    # routing in the request, so a role granted only on the project
    # sub-resource wouldn't authorize it (RBAC only inherits downward).
    openai_user = {
      scope = data.azurerm_cognitive_account.foundry.id
      role  = "Cognitive Services OpenAI User"
    }
  }
}

resource "azurerm_role_assignment" "func" {
  for_each             = local.func_role_assignments
  scope                = each.value.scope
  role_definition_name = each.value.role
  principal_id         = azurerm_windows_function_app.indexer.identity[0].principal_id
}
