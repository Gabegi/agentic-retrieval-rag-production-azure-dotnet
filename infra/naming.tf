locals {
  # ---------------------------------------------------------------------------
  # Naming convention, deduced from existing landing zone resources. Each
  # resource builds its own name from these primitives, inline, where it's
  # defined - this file only holds the shared convention, not concrete names.
  #
  #   Resource groups : cor-cap-<workload>-<env>-<region>-<instance>
  #   Most resources  : cor-<type>-cap-<env>-<region>-<instance>
  #   Multi-target       cor-<type>-<target>-cap-<env>-<region>-<instance>
  #     resources (eg      (private endpoints: cor-pep-ais-cap-dev-we-001)
  #     private            NIC = "<private endpoint name>_nic"
  #     endpoints)
  #   Subnets         : cor-snet-cap-<purpose>-<instance>   (no env/region)
  #   Storage accounts: cor + st + <purpose> + cap + <env> + <region>
  #                     (no dashes/instance - alphanumeric, <=24 chars)
  #
  # env_short: var.environment ("development"/"production", matching
  # 1-infra-deploy.yml's envName so the ADO Environment gate name and the
  # Terraform variable use the same spelling) maps to the compact "dev"/"prd"
  # already baked into every deployed resource name (see
  # .pipelines/1-infra-deploy.yml backendRgName: cor-cap-cicd-prd-we-001) -
  # that shorthand can't change without renaming live resources, so it stays
  # as its own, third, deliberately different spelling.
  # ---------------------------------------------------------------------------

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

  # Gates var.dev_allowed_ips to development regardless of what's in a given
  # .tfvars file - see variables.tf's dev_allowed_ips for the full rationale.
  dev_direct_access_ips = var.environment == "development" ? var.dev_allowed_ips : []
}
