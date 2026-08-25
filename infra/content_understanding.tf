# ---------------------------------------------------------------------------
# Content Understanding (custom "cap-pdf-layout" analyzer) - called through the
# existing Foundry multi-service AI Services account (data.azurerm_cognitive_
# account.foundry, see data.tf), not a standalone Cognitive Services account.
# There is no "Content Understanding" Azure resource type: CU is a data-plane
# capability on an AIServices-kind account, the same one that already serves
# the OpenAI deployments (ai_deployments.tf), Document Intelligence, Content
# Safety and AI Language (function_app.tf:120-135). Same endpoint, same private
# endpoint, same identity - no new azurerm_cognitive_account and no new
# azurerm_private_endpoint here.
#
# westeurope (envs/{dev,prod}.tfvars) is one of CU's supported regions, and the
# extraction gpt-5.4 deployment (ai_deployments.tf) is on CU's supported
# completion-model list, so neither the region nor the model needs to move.
# Analysis in docs/2608/260819/content-understanding-when-and-how.md, §5.
#
# This file replaces document_intelligence.tf, whose only content was the role
# assignment below, under the old name func_document_intelligence_user. Nothing
# about the grant itself changed - same scope, principal and role.
# ---------------------------------------------------------------------------

# Scoped to the account, not the project: the analyzer call goes to the
# account's own endpoint directly (config.ContentUnderstandingEndpoint =
# data.azurerm_cognitive_account.foundry.endpoint, function_app.tf), with no
# project routing in the request. RBAC only inherits downward, so a role
# granted on the project sub-resource doesn't authorize calls evaluated at the
# parent account - confirmed this was the cause of ExtractActivity's 403s.
resource "azurerm_role_assignment" "func_content_understanding_user" {
  scope = data.azurerm_cognitive_account.foundry.id
  # "Cognitive Services User" is the generic data-plane role for AAD-based
  # calls to this account. Its dataActions is the wildcard
  # Microsoft.CognitiveServices/*, so this single assignment is what authorizes
  # Content Understanding, Document Intelligence, Content Safety (Prompt
  # Shields) and AI Language alike - none of those has a dedicated built-in
  # role (checked via `az role definition list`: only OpenAI/Language/Speech
  # get their own). Do not add a second assignment for a new capability on this
  # account; an identical (scope, principal, role) tuple is rejected with
  # RoleAssignmentExists.
  role_definition_name = "Cognitive Services User"
  principal_id         = azurerm_windows_function_app.indexer.identity[0].principal_id
}

# ---------------------------------------------------------------------------
# CU PROVISIONING IS NOT OWNED HERE - decided 2026-08-21.
#
# The two per-resource bootstrap steps CU needs - the account's default
# model-deployment mapping, and the cap-pdf-layout analyzer itself - both
# belong to the application, behind POST /api/content-understanding/provision.
# See ContentUnderstandingProvisioner in
# src/AgenticRagApp.Infrastructure/Clients/ContentUnderstanding/, whose
# BuildAnalyzer() is the authoritative definition of the analyzer. The flags it
# sets are asserted by ContentUnderstandingVerifier next door; the two are kept
# in agreement by ContentUnderstandingProvisionerTests, not by review.
#
# This block used to carry instructions for doing them from here instead, as a
# raw REST / terraform_data step. Deleted rather than left standing as a second
# opinion: one writer per object, and the provisioner is the better writer.
# Reasons, so it doesn't get re-litigated:
#
#   1. ORDER. The defaults have to be written before the analyzer - analyzer
#      creation validates the deployments its Models block references, and an
#      analyzer-first order fails as "Model deployment not found", which reads
#      like a missing deployment rather than a missing mapping. One caller can
#      sequence that; two owners cannot.
#   2. ASYNC. Analyzer creation is a long-running operation. The provisioner
#      waits for Ready; a fire-and-forget PUT reports success for an analyzer
#      that is still building, and the first analyze call races it.
#   3. READBACK. The endpoint returns the *effective* analyzer and defaults,
#      read back from the service rather than echoed from the request. The
#      defaults especially are a merge-patched, account-global blob that
#      Terraform state could never see drift on.
#   4. NETWORK. The Function App reaches this account over its private
#      endpoint. Driving the data plane from Terraform instead would mean
#      opening this landing-zone-owned account's firewall to a hosted pipeline
#      agent on every run that touched it.
#
# The trap worth repeating here, because this file is where someone will look
# for it: the analyzer's Models block maps role -> MODEL name ({"completion":
# "gpt-5.4"}), while the account defaults map MODEL name -> deployment name
# ("gpt-5.4" -> var.openai_extraction_deployment). A deployment name in the
# analyzer fails at runtime, not at deploy time. See IndexerConfig's
# ContentUnderstandingCompletionModel.
#
# Teardown: nothing deletes the analyzer. It dies with the Foundry account,
# which this config doesn't own - so a partial teardown that keeps the account
# and removes the app leaves cap-pdf-layout behind. It bills nothing at rest,
# but it is worth knowing before someone goes looking for where it came from.
# ---------------------------------------------------------------------------
