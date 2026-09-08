locals {
  # Naming convention, deduced from existing landing-zone resources. Each resource composes its own
  # name inline from these primitives; this file holds only the shared parts.
  #   - Resource groups : con-cap-<workload>-<env>-<region>-<instance>
  #   - Most resources  : con-<type>-cap-<env>-<region>-<instance>
  #   - Multi-target    : con-<type>-<target>-cap-<env>-<region>-<instance>
  #                       (private endpoints, e.g. con-pep-ais-cap-dev-we-001; NIC = "<name>_nic")
  #   - Subnets         : con-snet-cap-<purpose>-<instance>   (no env/region)
  #   - Storage accounts: cor + st + <purpose> + cap + <env> + <region>  (alphanumeric, <=24 chars)
  #   - env_short maps var.environment ("development"/"production", matching the pipeline's envName)
  #     to the "dev"/"prd" baked into every live resource name - a third spelling that cannot change
  #     without renaming live resources.

  region_short = {
    westeurope = "we"
  }

  env_short = {
    development = "dev"
    production  = "prd"
  }

  region   = local.region_short[var.location]
  env      = local.env_short[var.environment]
  instance = "001"

  # Gates var.dev_allowed_ips to development regardless of the .tfvars contents - see variables.tf.
  dev_direct_access_ips = var.environment == "development" ? var.dev_allowed_ips : []
}
