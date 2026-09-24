# Linux App Service (.NET 10, P1v3) - the query API host, src/AgenticRagApp.Api: POST /api/query over
# the same IRagQueryService the Function App serves, for the OutSystems frontend. Reuses the Search,
# Foundry, App Insights and data-storage resources defined elsewhere; VNet-integrated for outbound,
# private endpoint for inbound, egress via the hub firewall like everything else. Deployed by
# pipeline.yml's deploy_api job (2026-09-16). What the API host needs and why:
# docs/2609/260916/query-api-project.md (D198) §4.

# The interim shared secret for POST /api/query (2026-09-24, D238 §2). Generated rather than
# taken as a variable so it never exists in git, in a .tfvars, in the public mirror or in a
# pipeline variable - it lives in Terraform state and in the app's configuration, and is read out
# of the app with the CLI when a consumer needs to be issued it (command in app_settings below).
#
# 48 alphanumeric characters, no specials: the value travels in an HTTP header and through
# whatever configuration UI the consuming organisation uses, and every quoting or escaping bug in
# that path is avoided by having nothing to escape. 48 chars of [A-Za-z0-9] is ~286 bits.
#
# keepers is empty on purpose: this must NOT regenerate on unrelated changes, because every
# regeneration is an unannounced hard cutover for every consumer. Rotation is deliberate -
# `terraform taint` then apply.
resource "random_password" "query_api_key" {
  length  = 48
  special = false
}

resource "azurerm_service_plan" "api" {
  name                = "con-plan-api-cap-${local.env}-${local.region}-${local.instance}"
  resource_group_name = azurerm_resource_group.api.name
  location            = var.location
  os_type             = "Linux"
  sku_name            = "P1v3"

  tags = local.common_tags
}

resource "azurerm_linux_web_app" "api" {
  name                      = "con-app-api-cap-${local.env}-${local.region}-${local.instance}"
  resource_group_name       = azurerm_resource_group.api.name
  location                  = var.location
  service_plan_id           = azurerm_service_plan.api.id
  virtual_network_subnet_id = azurerm_subnet.workload["api"].id
  # Same inbound model as function_app.tf (2026-09-16, was public_network_access_enabled = false):
  # public access on but deny-by-default on both the main and the SCM site, dev_allowed_ips allowed
  # in development, and the deploy pipeline adds a temporary SCM rule for its runner IP for the
  # duration of the zip deploy - a hosted agent has no VNet path, so an app that is
  # private-endpoint-only cannot be deployed to from the pipeline at all. The health probe after the
  # deploy does the same on the main site. The private endpoint below stays the path for callers
  # inside the network. Which public callers get a standing rule (OutSystems' egress, or none if an
  # API gateway fronts this) is the open decision in D198 §4.5.
  public_network_access_enabled = true
  https_only                    = true

  identity {
    type = "SystemAssigned"
  }

  site_config {
    application_stack {
      dotnet_version = "10.0"
    }
    always_on                         = true
    vnet_route_all_enabled            = true
    ip_restriction_default_action     = "Deny"
    scm_ip_restriction_default_action = "Deny"
    # GET /health is liveness only (no dependency probes) - see AgenticRagApp.Api/Program.cs - so a
    # Search or Foundry hiccup does not get the instance recycled.
    health_check_path = "/health"
    # RequiredWith health_check_path in azurerm 4.x - the plan of 2026-09-16 failed with "all of
    # `site_config.0.health_check_eviction_time_in_min,site_config.0.health_check_path` must be
    # specified". Provider range is 2-10; 10 is Azure's own default for the setting it writes
    # (WEBSITE_HEALTHCHECK_MAXPINGFAILURES), so this adds the argument without also changing
    # eviction behaviour. Largely inert at one instance anyway - App Service will not evict the
    # last healthy-or-not instance.
    health_check_eviction_time_in_min = 10

    # Rule names are capped at 32 chars; "dev-access-" (11) + a dashed IPv4 (up to 15) fits - see
    # function_app.tf for the failure that set the prefix.
    dynamic "ip_restriction" {
      for_each = var.environment == "development" ? var.dev_allowed_ips : []
      content {
        name       = "dev-access-${replace(ip_restriction.value, ".", "-")}"
        ip_address = "${ip_restriction.value}/32"
        action     = "Allow"
        priority   = 100
      }
    }

    # Same allowlist on the SCM site (Kudu, log stream). Terraform owns this list, so it also removes
    # a temp rule the deploy pipeline leaves behind if it dies between add and remove - and, the
    # same known race as on the Function App, an infra apply during an app deploy strips the
    # deploy's runner-IP rule and fails that deploy; rerun it.
    dynamic "scm_ip_restriction" {
      for_each = var.environment == "development" ? var.dev_allowed_ips : []
      content {
        name       = "dev-access-${replace(scm_ip_restriction.value, ".", "-")}"
        ip_address = "${scm_ip_restriction.value}/32"
        action     = "Allow"
        priority   = 100
      }
    }
  }

  # Everything IndexerConfig marks [Required] plus the host's own key, minus the indexing-only and
  # Functions-only settings (CONTENT_UNDERSTANDING_ENDPOINT, AzureWebJobsStorage*, the content
  # share, the timer's time zone) - AddAgenticRagAppInfrastructure fails fast at startup on any
  # missing key, so the list here is not optional. Same derivations as function_app.tf.
  app_settings = {
    "APPLICATIONINSIGHTS_CONNECTION_STRING" = azurerm_application_insights.main.connection_string
    # Drives IHostEnvironment.IsDevelopment(): console exporters next to the App Insights ones in
    # dev, visible in the log stream. The ASP.NET Core spelling of what function_app.tf sets as
    # DOTNET_ENVIRONMENT + AZURE_FUNCTIONS_ENVIRONMENT.
    "ASPNETCORE_ENVIRONMENT" = var.environment == "development" ? "Development" : "Production"
    # The per-query report (pipeline-reports/queries/...) - the API's only storage use, authorized
    # by azurerm_role_assignment.api["data_storage_contributor"] below.
    "STORAGE_ACCOUNT_URL"   = azurerm_storage_account.data.primary_blob_endpoint
    "SEARCH_ENDPOINT"       = "https://${azurerm_search_service.main.name}.search.windows.net"
    "SEARCH_INDEX_NAME"     = var.search_index_name
    "KNOWLEDGE_SOURCE_NAME" = var.knowledge_source_name
    "KNOWLEDGE_BASE_NAME"   = var.knowledge_base_name
    "OPENAI_ENDPOINT"       = data.azurerm_cognitive_account.foundry.endpoint
    "OPENAI_GPT_DEPLOYMENT" = var.openai_gpt_deployment
    "OPENAI_GPT_MODEL_NAME" = var.openai_gpt_model_name
    # [Required] on the shared IndexerConfig although the query path never embeds (the knowledge
    # base does that service-side) - the host does not start without it.
    "OPENAI_EMBEDDING_DEPLOYMENT" = var.openai_embedding_deployment
    # Same account as OPENAI_ENDPOINT: Content Safety (Prompt Shields, the prompt-injection guard)
    # and AI Language (PII guard) are data-plane paths on the Foundry account, authorized by the
    # cognitive_services_user grant below - see function_app.tf for the confirmation history.
    "CONTENT_SAFETY_ENDPOINT" = data.azurerm_cognitive_account.foundry.endpoint
    "LANGUAGE_ENDPOINT"       = data.azurerm_cognitive_account.foundry.endpoint
    # Query-time guards log but do not block while "true" - the same mode, for the same reason and
    # since the same date (2026-08-12), as on the Function App; absent would also mean true, this
    # is explicit so the mode is visible here. "false" restores enforcement on this host.
    "GUARDS_LOG_ONLY" = "true"
    # Interim inbound auth for POST /api/query (2026-09-24, D238 §2). Terraform is the only
    # writer of this value and the app only reads it: generated below, never in git, never in the
    # public mirror. Program.cs refuses to start without it, so removing this setting takes the
    # host down rather than quietly serving unauthenticated.
    #
    # To read the current token (to hand to a consuming organisation):
    #   az webapp config appsettings list -g <apiAppRg> -n <apiAppName> \
    #     --query "[?name=='QUERY_API_KEY'].value" -o tsv
    # To rotate it: taint random_password.query_api_key and apply. That is a hard cutover - every
    # consumer breaks until it has the new value, which is one of the reasons D238 §4 replaces
    # this with per-caller Entra credentials rather than growing a second shared token.
    "QUERY_API_KEY" = random_password.query_api_key.result
  }

  tags = local.common_tags

  # Azure auto-links App Insights from APPLICATIONINSIGHTS_CONNECTION_STRING and writes the
  # hidden-link tag back; without this every plan shows the auto-link as drift. Unlike
  # function_app.tf this ignores the tag only: site_config.application_insights_connection_string
  # exists on the Function App schemas, not on azurerm_linux_web_app, and terraform validate
  # rejects an ignore_changes path that is not in the schema (2026-09-16).
  lifecycle {
    ignore_changes = [
      tags["hidden-link: /app-insights-resource-id"],
    ]
  }
}

resource "azurerm_private_endpoint" "api" {
  name                          = "con-pep-api-cap-${local.env}-${local.region}-${local.instance}"
  location                      = var.location
  resource_group_name           = azurerm_resource_group.api.name
  subnet_id                     = data.azurerm_subnet.pe.id
  custom_network_interface_name = "con-pep-api-cap-${local.env}-${local.region}-${local.instance}_nic"

  private_service_connection {
    name                           = "con-pep-api-cap-${local.env}-${local.region}-${local.instance}-psc"
    private_connection_resource_id = azurerm_linux_web_app.api.id
    subresource_names              = ["sites"]
    is_manual_connection           = false
  }

  private_dns_zone_group {
    name                 = "default"
    private_dns_zone_ids = [data.azurerm_private_dns_zone.azurewebsites.id]
  }

  tags = local.common_tags
}

# Role assignments for the API's identity, looped like function_app.tf's. The query path's set,
# which is also what the eval identities in access.tf hold to call the same IRagQueryService
# in-process (eval_role_grants) - read on Search, OpenAI inference, the Foundry data-plane
# capabilities the guards use, and write on the data account for the per-query report. No Search
# write, no Storage beyond that, nothing on the Functions storage account. Foundry grants are
# account-scoped - RBAC only inherits downward.
locals {
  api_role_assignments = {
    search_index_reader = {
      scope = azurerm_search_service.main.id
      role  = "Search Index Data Reader"
    }
    openai_user = {
      scope = data.azurerm_cognitive_account.foundry.id
      role  = "Cognitive Services OpenAI User"
    }
    # Prompt Shields (PromptInjectionGuard) and AI Language PII detection (PiiGuard) - data-plane
    # capabilities of the Foundry account with no dedicated built-in role; this one grant covers
    # both. Do NOT add a second grant for another capability on this account: an identical
    # (scope, principal, role) tuple is rejected with RoleAssignmentExists. Added 2026-09-16.
    cognitive_services_user = {
      scope = data.azurerm_cognitive_account.foundry.id
      role  = "Cognitive Services User"
    }
    # RunReportWriter writes one blob per query into pipeline-reports (AssertContainerExistsAsync +
    # UploadJsonAsync). Account-scoped like the Function's grant; narrow to the container if the API
    # ever needs to be kept away from the other containers on this account. Added 2026-09-16.
    #
    # Deliberately no grant on the docs account (2026-09-21, D206): the API queries the search index
    # and writes its own report, and never reads the source corpus - so it also gets no
    # DOCUMENTS_STORAGE_ACCOUNT_URL setting below. The keyed "source-documents" client is registered
    # as a factory, so a host that never resolves it never builds it.
    data_storage_contributor = {
      scope = azurerm_storage_account.data.id
      role  = "Storage Blob Data Contributor"
    }
  }
}

resource "azurerm_role_assignment" "api" {
  for_each             = local.api_role_assignments
  scope                = each.value.scope
  role_definition_name = each.value.role
  principal_id         = azurerm_linux_web_app.api.identity[0].principal_id
}
