# ---------------------------------------------------------------------------
# Content Understanding - called through the existing Foundry multi-service AI
# Services account (data.azurerm_cognitive_account.foundry, see data.tf), not a
# standalone Cognitive Services account. There is no "Content Understanding"
# Azure resource type: CU is a data-plane capability on an AIServices-kind
# account, the same one that already serves the OpenAI deployments
# (ai_deployments.tf), Content Safety and AI Language (function_app.tf). Same
# endpoint, same private endpoint, same identity - no new
# azurerm_cognitive_account and no new azurerm_private_endpoint here.
#
# westeurope (envs/{dev,prod}.tfvars) is one of CU's supported regions.
# ---------------------------------------------------------------------------

# Scoped to the account, not the project: the analyze call goes to the account's
# own endpoint directly (config.ContentUnderstandingEndpoint =
# data.azurerm_cognitive_account.foundry.endpoint, function_app.tf), with no
# project routing in the request. RBAC only inherits downward, so a role granted
# on the project sub-resource doesn't authorize calls evaluated at the parent
# account - confirmed this was the cause of ExtractActivity's 403s.
resource "azurerm_role_assignment" "func_content_understanding_user" {
  scope = data.azurerm_cognitive_account.foundry.id
  # "Cognitive Services User" is the generic data-plane role for AAD-based calls
  # to this account. Its dataActions is the wildcard
  # Microsoft.CognitiveServices/*, so this single assignment is what authorizes
  # Content Understanding, Content Safety (Prompt Shields) and AI Language alike
  # - none of those has a dedicated built-in role (checked via `az role
  # definition list`: only OpenAI/Language/Speech get their own). Do not add a
  # second assignment for a new capability on this account; an identical (scope,
  # principal, role) tuple is rejected with RoleAssignmentExists.
  role_definition_name = "Cognitive Services User"
  principal_id         = azurerm_windows_function_app.indexer.identity[0].principal_id
}

# ---------------------------------------------------------------------------
# NO MANUAL STEP - the account-wide default model->deployment mapping is
# verified (and fixed if wrong) by the app itself, once, at host startup.
#
# The application submits against the PREBUILT analyzer prebuilt-documentSearch
# (hardcoded in ContentAnalysisClient), so there is no custom analyzer to create
# - the cap-pdf-layout analyzer, the provisioner that created it and the
# verifier that checked it were all deleted on 2026-08-25.
#
# What the prebuilts still require (SDK Sample00: "required one-time setup per
# Foundry resource") is the default model->deployment mapping. That is handled
# by ContentUnderstandingDefaultsSetup (AgenticRagApp.Infrastructure), an
# IHostedService: GetDefaults -> compare against the app's configured names ->
# UpdateDefaults ONLY when an entry is missing or wrong. Read-then-write-if-
# needed, because the mapping is account-global state shared with every other
# consumer of this account; UpdateDefaults is merge-patch, so entries for
# models this app does not use are never touched. It runs under the function
# app's managed identity, whose "Cognitive Services User" assignment above is
# exactly what authorizes it - no human role grant, no PATCH by hand.
#
# Keys are MODEL names, values are DEPLOYMENT names: gpt-5.4-mini ->
# var.openai_mini_deployment (whose value is still the string "gpt-4.1-mini" -
# a deployment name deliberately left unrenamed when the model moved off
# gpt-4.1-mini on 2026-08-27, see ai_deployments.tf), text-embedding-3-large ->
# var.openai_embedding_deployment ("embedding-3-large" - note the deployment
# name is NOT the model name here; an earlier version of this comment showed
# the wrong value). Not driven from Terraform for the original reason: data
# plane writes from a hosted pipeline agent would require opening this
# landing-zone-owned account's firewall on every run.
# ---------------------------------------------------------------------------
