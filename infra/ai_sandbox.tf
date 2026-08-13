# ---------------------------------------------------------------------------
# Everything that makes the sandbox project (ai_project.tf) usable, and
# everything that keeps it contained: its own model deployment, its own
# content filter, and role grants scoped to the project and nothing else.
#
# All of it is development-only, matching ai_project.tf's gate.
#
# What actually enforces the boundary, in descending order of how much it can
# be trusted:
#
#   1. Deployment capacity. A hard TPM ceiling Azure enforces server-side.
#      This is the one guarantee here that cannot be argued around: whatever
#      is run in the sandbox cannot starve the query/extraction/evaluation
#      deployments, which were raised to 200 in response to real 429s during
#      eval runs (see ai_deployments.tf, 2026-07-29/30).
#   2. Absence of grants. No role on azurerm_search_service.main or
#      azurerm_storage_account.data is given to sandbox users or to the
#      project's identity, and Azure RBAC is deny-by-default, so Contoso
#      content is unreachable from here. Note this is isolation by OMISSION -
#      it holds exactly as long as nobody adds a convenience grant later.
#      Anything in this repo that loops over "all developers" must not pick
#      up var.sandbox_user_object_ids.
#   3. Content filter. Defence in depth against misuse, not an access control.
#
# Not enforced here, because it cannot be on a shared account: network
# posture, account-level diagnostics, and the subscription quota pool are all
# account-scoped and owned by the landing-zone team. See
# docs/2608/260807/foundry-sandbox-isolation-plan.md.
# ---------------------------------------------------------------------------

# --- Content filter --------------------------------------------------------
# The account exposes only two built-in policies, Microsoft.Default and
# Microsoft.DefaultV2 (verified 2026-08-07 via the raiPolicies ARM endpoint).
# Neither is stricter than the other - DefaultV2 is just the current default
# for new deployments - so a tighter filter has to be a custom policy.
#
# Custom policies are account-scoped children, like projects and deployments,
# which is why this can be created without owning the account itself. It is
# referenced by name (not ID) from the deployment below - that is how the
# Cognitive Services API models the relationship.
#
# Difference from the default: severity thresholds drop from Medium to Low on
# all four harm categories in both directions, and the optional prompt-shield
# and protected-material filters are switched on and set to block rather than
# annotate. This is deliberately more aggressive than the app's own
# deployments, which stay on the account default - a sandbox refusing too much
# is an inconvenience, whereas the app refusing too much is a defect.
resource "azapi_resource" "sandbox_rai_policy" {
  count = var.environment == "development" ? 1 : 0

  type      = "Microsoft.CognitiveServices/accounts/raiPolicies@2025-06-01"
  name      = "con-cap-sandbox-strict"
  parent_id = data.azurerm_cognitive_account.foundry.id

  body = {
    properties = {
      mode           = "Blocking"
      basePolicyName = "Microsoft.DefaultV2"

      # Filter names and severityThreshold values are validated by ARM, not by
      # Terraform - a typo here surfaces at apply time, not at plan time.
      contentFilters = [
        { name = "Hate", severityThreshold = "Low", blocking = true, enabled = true, source = "Prompt" },
        { name = "Hate", severityThreshold = "Low", blocking = true, enabled = true, source = "Completion" },
        { name = "Sexual", severityThreshold = "Low", blocking = true, enabled = true, source = "Prompt" },
        { name = "Sexual", severityThreshold = "Low", blocking = true, enabled = true, source = "Completion" },
        { name = "Violence", severityThreshold = "Low", blocking = true, enabled = true, source = "Prompt" },
        { name = "Violence", severityThreshold = "Low", blocking = true, enabled = true, source = "Completion" },
        { name = "Selfharm", severityThreshold = "Low", blocking = true, enabled = true, source = "Prompt" },
        { name = "Selfharm", severityThreshold = "Low", blocking = true, enabled = true, source = "Completion" },

        # Prompt shields and protected-material detection - off or
        # annotate-only under the built-in defaults.
        { name = "Jailbreak", blocking = true, enabled = true, source = "Prompt" },
        { name = "Indirect Attack", blocking = true, enabled = true, source = "Prompt" },
        { name = "Protected Material Text", blocking = true, enabled = true, source = "Completion" },
        { name = "Protected Material Code", blocking = true, enabled = true, source = "Completion" },
      ]
    }
  }
}

# --- Model deployment ------------------------------------------------------
# Separate from local.openai_deployments in ai_deployments.tf on purpose: that
# map has no environment gate and its entries are part of the application, so
# folding a development-only sandbox into it would either create the sandbox
# in production or push a conditional into a map that currently has none.
#
# gpt-5.4-mini rather than the gpt-5.4 the app uses, for two reasons. It is a
# markedly cheaper and weaker model, which is the right default for
# experimentation nobody is monitoring. More importantly quota in Azure is
# pooled per model, so putting the sandbox on a model this repo does not
# otherwise deploy means the two cannot compete for quota even in principle:
# OpenAI.GlobalStandard.gpt-5.4-mini was 0/1000 K TPM in westeurope on
# 2026-08-07, entirely unused, and is a separate pool from
# OpenAI.GlobalStandard.gpt-5.4 (240/1000, mostly this app).
#
# That makes the model choice a second, independent isolation lever on top of
# the capacity ceiling below - so if this is ever pointed at a stronger model,
# check the target's own quota pool first rather than assuming the ceiling
# alone is protection.
#
# On picking a replacement model: filter on lifecycleStatus, NOT on the
# retirement date. gpt-4o-mini was tried here first and rejected by ARM at
# apply time with ServiceModelDeprecating - it has a 2027 retirement date but
# lifecycleStatus "Deprecating", which already blocks NEW deployments (same
# thing that rules out gpt-4.1, see ai_deployments.tf). So do:
#   az cognitiveservices model list --location westeurope \
#     --query "[?model.lifecycleStatus=='GenerallyAvailable'].{n:model.name,v:model.version}"
# gpt-5.4-mini is GA with the longest retirement horizon of the small models
# available here (2027-09-21), so this shouldn't need revisiting soon.
resource "azurerm_cognitive_deployment" "sandbox" {
  count = var.environment == "development" ? 1 : 0

  name                 = var.openai_sandbox_deployment
  cognitive_account_id = data.azurerm_cognitive_account.foundry.id
  rai_policy_name      = azapi_resource.sandbox_rai_policy[0].name

  # Pinned rather than auto-upgrading: a sandbox that silently changes model
  # version underneath an experiment makes the experiment unreproducible, and
  # nobody is watching this deployment for behaviour changes.
  version_upgrade_option = "NoAutoUpgrade"

  model {
    format  = "OpenAI"
    name    = "gpt-5.4-mini"
    version = "2026-03-17"
  }

  sku {
    name     = "GlobalStandard"
    capacity = var.sandbox_deployment_capacity
  }

  # Explicitly off, and load-bearing. Dynamic throttling lets a deployment
  # burst above its provisioned capacity when the account has spare capacity
  # going - which is exactly the behaviour this deployment exists to prevent,
  # since the spare capacity it would borrow is what the eval deployments need
  # during a run. Leaving this unset would make the capacity ceiling above a
  # soft target instead of a wall.
  dynamic_throttling_enabled = false
}

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
locals {
  sandbox_principal_ids = (
    var.environment == "development" ? toset(var.sandbox_user_object_ids) : toset([])
  )
}

resource "azurerm_role_assignment" "sandbox_user_ai_developer" {
  for_each             = local.sandbox_principal_ids
  scope                = azapi_resource.sandbox[0].id
  role_definition_name = "Azure AI Developer"
  principal_id         = each.value
}

resource "azurerm_role_assignment" "sandbox_user_openai_user" {
  for_each             = local.sandbox_principal_ids
  scope                = azapi_resource.sandbox[0].id
  role_definition_name = "Cognitive Services OpenAI User"
  principal_id         = each.value
}
