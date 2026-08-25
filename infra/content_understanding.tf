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
# THE ONE MANUAL STEP - the account-wide default model->deployment mapping.
#
# The application submits against the PREBUILT analyzer prebuilt-documentSearch
# (hardcoded in ContentAnalysisClient), so there is no custom analyzer to create
# any more - the cap-pdf-layout analyzer, the provisioner that created it and the
# verifier that checked it were all deleted on 2026-08-25 when the client was cut
# back to the SDK sample shape.
#
# What did NOT go away is the prerequisite the prebuilt analyzers carry: they
# resolve their models through this account's default model->deployment mapping,
# and there is nothing left in the app that writes it. AnalyzeBinaryAsync has no
# per-request modelDeployments parameter - only the Analyze(inputs) overload does
# - so it cannot be passed per call either. Miss the mapping and the first
# analyze call fails with "Model deployment not found".
#
# Not done from here for the same reason it never was: driving this data plane
# from Terraform means opening this landing-zone-owned account's firewall to a
# hosted pipeline agent on every run that touches it. Run it once, by hand,
# against the deployments in ai_deployments.tf:
#
#   PATCH {foundry_endpoint}/contentunderstanding/defaults?api-version=2025-11-01
#   Authorization: Bearer <token for https://cognitiveservices.azure.com>
#   Content-Type: application/json
#   {
#     "modelDeployments": {
#       "gpt-4.1-mini":           "gpt-4.1-mini",
#       "text-embedding-3-large": "embedding-3-large"
#     }
#   }
#
# Keys are MODEL names, values are DEPLOYMENT names (var.openai_mini_deployment
# and var.openai_embedding_deployment). The mapping is account-global and
# merge-patched, so it is visible to every other consumer of this account -
# which is also why Terraform state could never see drift on it.
# ---------------------------------------------------------------------------
