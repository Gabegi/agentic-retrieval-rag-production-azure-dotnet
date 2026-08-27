# ===========================================================================
# COMMENTED OUT 2026-08-21 - see the banner in ai_project.tf. The commented
# resources were destroyed by the first apply after that date; nothing in
# this file exists in Azure.
#
# DELETED OUTRIGHT 2026-08-27 (recover from git history if ever restored):
#   - azurerm_cognitive_deployment.sandbox   (the gpt-5.4-mini deployment)
#   - azapi_resource.sandbox_rai_policy      (the custom strict content
#     filter; existed only to be referenced by that deployment)
# together with their variables (openai_sandbox_deployment,
# sandbox_deployment_capacity) and the sandbox_deployment_name output.
# Removal was prompted by the same day's move of ai_deployments.tf's `mini`
# entry (Content Understanding) onto gpt-5.4-mini at capacity 1000 - the
# whole pool - which made this deployment impossible to enable and voided
# its "a model the app does not use" isolation rationale.
#
# What remains below is only the access scaffolding for the sandbox project.
# Restoring a WORKING sandbox now means: uncomment this file, ai_project.tf
# and outputs.tf together, AND re-author a model deployment - on a model
# whose quota pool the app does not consume (check ai_deployments.tf first),
# with a hard capacity ceiling and dynamic_throttling_enabled = false. The
# deleted resources in git history are the template.
# ===========================================================================

# ---------------------------------------------------------------------------
# Role grants for the sandbox project (ai_project.tf), scoped to the project
# and nothing else. Development-only, matching ai_project.tf's gate.
#
# What actually enforces the boundary:
#
#   1. Absence of grants. No role on azurerm_search_service.main or
#      azurerm_storage_account.data is given to sandbox users or to the
#      project's identity, and Azure RBAC is deny-by-default, so Contoso
#      content is unreachable from here. Note this is isolation by OMISSION -
#      it holds exactly as long as nobody adds a convenience grant later.
#      Anything in this repo that loops over "all developers" must not pick
#      up var.sandbox_user_object_ids.
#   2. (Deleted 2026-08-27 with the deployment:) a hard per-deployment TPM
#      ceiling, and a stricter content filter. A restored sandbox needs both
#      re-authored - see the banner above.
#
# Not enforced here, because it cannot be on a shared account: network
# posture, account-level diagnostics, and the subscription quota pool are all
# account-scoped and owned by the landing-zone team. See
# docs/2608/260807/foundry-sandbox-isolation-plan.md.
# ---------------------------------------------------------------------------

# --- Access ----------------------------------------------------------------
# Scoped to the project resource ID, never to the account. Both roles are
# needed for the project to be usable at all: Azure AI Developer to work in
# it (agents, threads, playground), Cognitive Services OpenAI User to issue
# inference calls.
#
# OPEN QUESTION, unresolved as of 2026-08-07: Cognitive Services authorizes
# inference at ACCOUNT scope, and it has not been tested whether a grant at
# project scope is sufficient to call a deployment through the project
# endpoint. If it turns out not to be, the only way to make sandbox users
# functional is Cognitive Services OpenAI User at account scope - which
# reaches every deployment on the account, including the eval ones, and
# collapses the isolation this file is built around. That grant is
# deliberately not written here. If someone reaches for it, the honest move is
# to stop calling this project isolated, not to add it quietly.
#
# Per-deployment role assignments are NOT an alternative - they are not an
# enforced inference boundary and must not be relied on as one.
#
# var.sandbox_user_object_ids takes AAD object IDs. An Entra GROUP's object ID
# works here as well as a user's and is the better shape if this outlives the
# current push: adding a person then becomes a group membership change with no
# terraform apply and no PR.
# locals {
#   sandbox_principal_ids = (
#     var.environment == "development" ? toset(var.sandbox_user_object_ids) : toset([])
#   )
# }
#
# resource "azurerm_role_assignment" "sandbox_user_ai_developer" {
#   for_each             = local.sandbox_principal_ids
#   scope                = azapi_resource.sandbox[0].id
#   role_definition_name = "Azure AI Developer"
#   principal_id         = each.value
# }
#
# resource "azurerm_role_assignment" "sandbox_user_openai_user" {
#   for_each             = local.sandbox_principal_ids
#   scope                = azapi_resource.sandbox[0].id
#   role_definition_name = "Cognitive Services OpenAI User"
#   principal_id         = each.value
# }
