# The sandbox resources are development-only (ai_project.tf, ai_sandbox.tf),
# so every output here is null in production rather than absent - one() over
# the count'd resource, so prod plans don't fail on an index that isn't there.

output "sandbox_project_id" {
  description = "Resource ID of the sandbox Foundry project (ai_project.tf). Null outside development. This is the only scope sandbox users hold any role on."
  value       = one(azapi_resource.sandbox[*].id)
}

# Assigned by Azure, not composed here - hence read back off the response
# rather than built from the account name and project name.
output "sandbox_project_endpoint" {
  description = "AI Foundry API (data-plane) endpoint of the sandbox project. Null outside development. Reachable only over the account's private endpoint - the account is publicNetworkAccess: Disabled with an empty IP allowlist, so this resolving from a laptop is not a given."
  value       = one(azapi_resource.sandbox[*].output.properties.endpoints["AI Foundry API"])
}

output "sandbox_project_principal_id" {
  description = "Object ID of the sandbox project's system-assigned identity - RBAC on the account has to be granted to this explicitly, it does not inherit from the project scope. Null outside development. Deliberately holds no role on the search service or data storage account."
  value       = one(azapi_resource.sandbox[*].identity[0].principal_id)
}

output "sandbox_deployment_name" {
  description = "Name of the sandbox's own gpt-5.4-mini deployment (ai_sandbox.tf). Null outside development. Its capacity is a hard TPM ceiling, which is what stops sandbox use from 429ing the app's eval runs."
  value       = one(azurerm_cognitive_deployment.sandbox[*].name)
}
