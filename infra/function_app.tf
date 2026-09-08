# Windows Function App (dotnet-isolated, EP1) running the durable indexing pipeline. Reuses the
# storage, App Insights, Search and Foundry resources defined elsewhere; VNet-integrated for
# outbound, private endpoint for inbound, egress via the hub firewall like everything else.

resource "azurerm_service_plan" "func" {
  name                = "con-plan-func-cap-${local.env}-${local.region}-${local.instance}"
  resource_group_name = data.azurerm_resource_group.data.name
  location            = var.location
  os_type             = "Windows"
  # Elastic Premium, not P1v3 - the durable pipeline runs on an Elastic plan by earlier decision.
  sku_name = "EP1"

  tags = local.common_tags
}

resource "azurerm_windows_function_app" "indexer" {
  name                          = "con-func-idx-cap-${local.env}-${local.region}-${local.instance}"
  resource_group_name           = data.azurerm_resource_group.data.name
  location                      = var.location
  service_plan_id               = azurerm_service_plan.func.id
  storage_account_name          = azurerm_storage_account.func.name
  storage_uses_managed_identity = true
  virtual_network_subnet_id     = azurerm_subnet.workload["func"].id
  # Deny-by-default. In development dev_allowed_ips is allowed on the main site and (since
  # 2026-08-25) the SCM site. The app-deploy pipeline (base/deploy-function-app.yml) runs on a
  # hosted agent with no VNet access, so it adds a temporary SCM allow rule for its runner IP right
  # before the zip deploy and removes it right after.
  public_network_access_enabled = true
  # storage_uses_managed_identity covers AzureWebJobsStorage/Durable (blob/queue/table) only. The
  # EP1 content share stays key-based (Azure Files has no managed-identity auth) and is reached over
  # its private endpoint (azurerm_private_endpoint.storage["stfunc-file"], storage.tf) via
  # WEBSITE_CONTENTOVERVNET - see app_settings.

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

    # Azure caps IpSecurityRestriction.Name at 32 chars and a dashed IPv4 is up to 15, so the
    # prefix gets 17. "dev-direct-access-" (18) failed the apply on 192.168.100.151 on 2026-08-12;
    # "dev-access-" (11) fits any address.
    dynamic "ip_restriction" {
      for_each = var.environment == "development" ? var.dev_allowed_ips : []
      content {
        name       = "dev-access-${replace(ip_restriction.value, ".", "-")}"
        ip_address = "${ip_restriction.value}/32"
        action     = "Allow"
        priority   = 100
      }
    }

    # Same allowlist on the SCM site (Kudu, log stream) - one list to maintain. Terraform owns the
    # SCM rule list, so it also removes any temp rule the deploy pipeline leaves behind if it dies
    # between add and remove. Known race: an infra apply during an app deploy strips the deploy's
    # runner-IP rule and fails that deploy - rerun it.
    dynamic "scm_ip_restriction" {
      for_each = var.environment == "development" ? var.dev_allowed_ips : []
      content {
        name       = "dev-access-${replace(scm_ip_restriction.value, ".", "-")}"
        ip_address = "${scm_ip_restriction.value}/32"
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
    # Drives IHostEnvironment.IsDevelopment() (dev-only diagnostics, IRunReportWriter). Both set on
    # purpose: the isolated-worker host takes its environment from AZURE_FUNCTIONS_ENVIRONMENT, and
    # DOTNET_ENVIRONMENT alone was confirmed insufficient (pipeline-reports stayed empty).
    "DOTNET_ENVIRONMENT"          = var.environment == "development" ? "Development" : "Production"
    "AZURE_FUNCTIONS_ENVIRONMENT" = var.environment == "development" ? "Development" : "Production"
    # Durable Functions over managed identity - no connection string.
    "AzureWebJobsStorage__accountName" = azurerm_storage_account.func.name
    "AzureWebJobsStorage__credential"  = "managedidentity"
    # Content share: key-based, over the private endpoint (see the resource comment above).
    "WEBSITE_CONTENTOVERVNET"                  = "1"
    "WEBSITE_CONTENTAZUREFILECONNECTIONSTRING" = azurerm_storage_account.func.primary_connection_string
    "WEBSITE_CONTENTSHARE"                     = azurerm_storage_share.func_content.name
    "ProtocolsStorage__blobServiceUri"         = azurerm_storage_account.data.primary_blob_endpoint
    "STORAGE_ACCOUNT_URL"                      = azurerm_storage_account.data.primary_blob_endpoint
    "SEARCH_ENDPOINT"                          = "https://${azurerm_search_service.main.name}.search.windows.net"
    "OPENAI_ENDPOINT"                          = data.azurerm_cognitive_account.foundry.endpoint
    "OPENAI_EMBEDDING_DEPLOYMENT"              = var.openai_embedding_deployment
    "OPENAI_GPT_DEPLOYMENT"                    = var.openai_gpt_deployment
    "OPENAI_GPT_MODEL_NAME"                    = var.openai_gpt_model_name
    "OPENAI_EXTRACTION_DEPLOYMENT"             = var.openai_extraction_deployment
    # Read only by ContentUnderstandingDefaultsSetup, which writes the account-wide default
    # model->deployment mapping at host startup (see the cognitive_services_user grant below).
    "OPENAI_MINI_DEPLOYMENT" = var.openai_mini_deployment
    # Same account as OPENAI_ENDPOINT - Content Understanding is a data-plane path on it, not a
    # separate resource (see the cognitive_services_user grant below). The only extraction-backend
    # setting since the Document Intelligence path was removed. Required: AddPdfIndexing throws at
    # startup without it.
    "CONTENT_UNDERSTANDING_ENDPOINT" = data.azurerm_cognitive_account.foundry.endpoint
    # Same account again: Content Safety (Prompt Shields) and AI Language (PII) both answer on it
    # (confirmed 2026-08-06 - PermissionDenied, not 404), so no separate resources. Authorized by
    # azurerm_role_assignment.func["cognitive_services_user"] below.
    "CONTENT_SAFETY_ENDPOINT" = data.azurerm_cognitive_account.foundry.endpoint
    "LANGUAGE_ENDPOINT"       = data.azurerm_cognitive_account.foundry.endpoint
    "SEARCH_INDEX_NAME"       = var.search_index_name
    "KNOWLEDGE_SOURCE_NAME"   = var.knowledge_source_name
    "KNOWLEDGE_BASE_NAME"     = var.knowledge_base_name

    # Windows-only: TimerTrigger crons (ScheduledIndexing's daily 17:00) follow Dutch wall-clock
    # across DST instead of drifting with UTC.
    "WEBSITE_TIME_ZONE" = "W. Europe Standard Time"
  }

  tags = local.common_tags

  # Azure auto-links App Insights from APPLICATIONINSIGHTS_CONNECTION_STRING (hidden-link tag +
  # site_config connection string). Without this, every plan shows the auto-link as drift.
  lifecycle {
    ignore_changes = [
      tags["hidden-link: /app-insights-resource-id"],
      site_config[0].application_insights_connection_string,
    ]
  }
}

# Content share every EP1 Function App needs.
resource "azurerm_storage_share" "func_content" {
  name               = "con-func-idx-cap-${local.env}-${local.region}-${local.instance}"
  storage_account_id = azurerm_storage_account.func.id
  quota              = 100
}

# Large Durable payloads between activities (extracted docs, chunks) - on the func account, not the
# shared data account.
resource "azurerm_storage_container" "indexing_pipeline" {
  name                  = "indexing-pipeline"
  storage_account_id    = azurerm_storage_account.func.id
  container_access_type = "private"
}

resource "azurerm_private_endpoint" "func" {
  name                          = "con-pep-func-cap-${local.env}-${local.region}-${local.instance}"
  location                      = var.location
  resource_group_name           = data.azurerm_resource_group.data.name
  subnet_id                     = data.azurerm_subnet.pe.id
  custom_network_interface_name = "con-pep-func-cap-${local.env}-${local.region}-${local.instance}_nic"

  private_service_connection {
    name                           = "con-pep-func-cap-${local.env}-${local.region}-${local.instance}-psc"
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

# Intermediate payloads: expire after 7 days rather than accumulate on an account with no other
# cleanup.
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

# Role assignments for the indexer's identity, looped - only scope/role vary. Foundry grants are
# account-scoped, not project-scoped: the app calls the account endpoint with no project routing,
# and RBAC only inherits downward.
locals {
  func_role_assignments = {
    # Account-level Owner is what Microsoft's identity-based-connection docs require for
    # AzureWebJobsStorage/Durable (the host creates its own containers at runtime).
    # indexing_pipeline_contributor is already covered by it; kept additive in case this narrows.
    storage_owner = {
      scope = azurerm_storage_account.func.id
      role  = "Storage Blob Data Owner"
    }
    indexing_pipeline_contributor = {
      scope = azurerm_storage_container.indexing_pipeline.id
      role  = "Storage Blob Data Contributor"
    }
    # Durable orchestration state lives in queues and tables.
    storage_queue_contributor = {
      scope = azurerm_storage_account.func.id
      role  = "Storage Queue Data Contributor"
    }
    storage_table_contributor = {
      scope = azurerm_storage_account.func.id
      role  = "Storage Table Data Contributor"
    }
    # Reads source documents, writes chunks/reports/state.
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
    openai_user = {
      scope = data.azurerm_cognitive_account.foundry.id
      role  = "Cognitive Services OpenAI User"
    }
    # Content Understanding, Content Safety (Prompt Shields) and AI Language all run as data-plane
    # capabilities of this one account - there is no CU resource type and no separate Content
    # Safety/Language resource (confirmed 2026-08-06: both return PermissionDenied, not 404).
    # "Cognitive Services User" has the wildcard dataActions Microsoft.CognitiveServices/*, so this
    # single grant authorizes all three; none has a dedicated built-in role (only
    # OpenAI/Language/Speech do). Do NOT add a second grant for a new capability here - an identical
    # (scope, principal, role) tuple is rejected with RoleAssignmentExists.
    #
    # CU specifics, since this grant is what makes it work:
    #   - Analyzer: the prebuilt "prebuilt-documentSearch" (hardcoded in ContentAnalysisClient). No
    #     custom analyzer - cap-pdf-layout, its provisioner and verifier were deleted 2026-08-25.
    #   - The prebuilt needs the account-wide default model->deployment mapping (SDK Sample00:
    #     "required one-time setup per Foundry resource"), written by
    #     ContentUnderstandingDefaultsSetup (IHostedService) at host startup: GetDefaults, compare,
    #     UpdateDefaults only when an entry is missing or wrong. Merge-patch, so other consumers'
    #     entries on this shared account are never touched. Runs under this identity - no human
    #     grant, no manual PATCH.
    #   - Mapping keys are MODEL names, values are DEPLOYMENT names:
    #       gpt-5.4-mini           -> var.openai_mini_deployment      ("gpt-4.1-mini", name frozen)
    #       text-embedding-3-large -> var.openai_embedding_deployment ("embedding-3-large")
    #   - Not driven from Terraform: a data-plane write from a hosted pipeline agent would need the
    #     landing-zone-owned account's firewall opened on every run.
    cognitive_services_user = {
      scope = data.azurerm_cognitive_account.foundry.id
      role  = "Cognitive Services User"
    }
  }
}

resource "azurerm_role_assignment" "func" {
  for_each             = local.func_role_assignments
  scope                = each.value.scope
  role_definition_name = each.value.role
  principal_id         = azurerm_windows_function_app.indexer.identity[0].principal_id
}
