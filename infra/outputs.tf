output "sandbox_project_id" {
  description = "Resource ID of the sandbox Foundry project (ai_project.tf)"
  value       = azapi_resource.sandbox.id
}

# Assigned by Azure, not composed here - hence read back off the response
# rather than built from the account name and project name.
output "sandbox_project_endpoint" {
  description = "AI Foundry API (data-plane) endpoint of the sandbox project"
  value       = azapi_resource.sandbox.output.properties.endpoints["AI Foundry API"]
}

output "sandbox_project_principal_id" {
  description = "Object ID of the sandbox project's system-assigned identity - RBAC on the account has to be granted to this explicitly, it does not inherit from the project scope"
  value       = azapi_resource.sandbox.identity[0].principal_id
}
