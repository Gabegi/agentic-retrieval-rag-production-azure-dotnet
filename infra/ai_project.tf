# ---------------------------------------------------------------------------
# Foundry project, scoped under the existing Foundry AI Services account
# (data.azurerm_cognitive_account.foundry, see data.tf). A project is a child
# resource - Microsoft.CognitiveServices/accounts/projects - not a standalone
# resource, so it inherits the account's endpoint, model deployments
# (ai_deployments.tf) and network restrictions, and only adds its own scope,
# identity and agent/thread state on top.
#
# azapi rather than azurerm: azurerm's azurerm_ai_foundry_project targets the
# older hub-based Foundry (Microsoft.MachineLearningServices/workspaces with
# kind = "Project"), which is a different resource type from the account-scoped
# project used here. There is no azurerm resource for the latter, so this goes
# through the ARM REST passthrough provider - same reason as the private
# endpoint connection approval in search.tf.
#
# Prerequisite, verified 2026-08-06 on cor-ais-cap-dev-we-001:
# properties.allowProjectManagement = true on the account. Without it ARM
# rejects project creation. That flag is owned by the landing-zone team, not
# this config.
#
# Development-only, via count - same gate and same reasoning as dev_access.tf
# and dev_allowed_ips. The sandbox exists so people can experiment against
# models without touching the app's own project or deployments; there is no
# version of that which belongs on the production account. The deployment and
# role grants that go with it are in ai_sandbox.tf and gate identically.
# ---------------------------------------------------------------------------

resource "azapi_resource" "sandbox" {
  count = var.environment == "development" ? 1 : 0

  type = "Microsoft.CognitiveServices/accounts/projects@2025-06-01"

  # Deliberately follows the naming of the project the landing-zone team
  # already created on this account (cor-cap-dvt-dev) rather than the
  # cor-<type>-cap-<env>-<region>-<instance> convention in naming.tf - the
  # project name ends up verbatim in the data-plane endpoint below, and the
  # two projects on one account should read as siblings there.
  name      = "cor-cap-sandbox-${local.env}"
  parent_id = data.azurerm_cognitive_account.foundry.id
  location  = var.location

  # purpose=sandbox marks this and its deployment (ai_sandbox.tf) as the one
  # scope on this account that untrusted experimentation runs in - so a future
  # reader can tell at a glance which resources are deliberately outside the
  # app's data boundary.
  tags = merge(local.common_tags, { purpose = "sandbox" })

  # The project gets its own principal, separate from the account's. Note that
  # Cognitive Services RBAC does not inherit upward from a project to its
  # account (see the comment on search_openai_user in search.tf), so anything
  # this identity needs on the account itself has to be granted explicitly.
  identity {
    type = "SystemAssigned"
  }

  body = {
    properties = {
      displayName = "Contoso AI - Sandbox (${local.env})"
      description = "Sandbox project for experimentation - managed by Terraform"
    }
  }

  # isDefault is left unset on purpose: cor-cap-dvt-dev is currently the
  # account's default project, and claiming that flag here would silently move
  # it off a resource this config doesn't own.

  # Exposes properties.endpoints["AI Foundry API"] for the output in
  # outputs.tf - the endpoint is assigned by Azure, not composed by us.
  response_export_values = ["properties.endpoints", "properties.internalId"]
}
