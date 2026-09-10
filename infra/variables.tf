variable "environment" {
  type        = string
  description = "Environment name (development, production) - matches base/deploy-azure-infrastructure.yml's envName. See naming.tf's env_short for the separate dev/prd shorthand baked into resource names."
}

variable "location" {
  type        = string
  description = "Azure region"
}

variable "project" {
  type        = string
  description = "Project name used in resource naming"
}

variable "tags" {
  type        = map(string)
  description = "Tags applied to all resources"
  default     = {}
}

variable "openai_embedding_deployment" {
  type        = string
  description = "Deployment name for the text-embedding-3-large model on the Foundry AI Services account. Also the value Content Understanding's default model->deployment mapping points text-embedding-3-large at (function_app.tf)."
  default     = "embedding-3-large"
}

variable "openai_gpt_deployment" {
  type        = string
  description = "Deployment name for the query API's GPT model - runs gpt-5.4 (ai_deployments.tf). The name still says gpt-4.1 on purpose: renaming a deployment forces destroy+recreate, so names are frozen and models move under them."
  default     = "gpt-4.1-query"
}

variable "openai_extraction_deployment" {
  type        = string
  description = "Deployment name for the indexing/extraction pipeline's GPT model - runs gpt-5.4 (ai_deployments.tf). Name frozen at gpt-4.1 for the same reason as openai_gpt_deployment."
  default     = "gpt-4.1-extraction"
}

variable "openai_eval_deployment" {
  type        = string
  description = "Deployment name for the eval judge model - runs gpt-5.1, deliberately a different model from querying/extraction (ai_deployments.tf). Name frozen from the gpt-4o era for the same reason as openai_gpt_deployment."
  default     = "gpt-4o-eval"
}

variable "openai_gpt_model_name" {
  type        = string
  description = "Human-readable model family name for the query/extraction GPT deployment (distinct from the deployment name)"
  default     = "gpt-5.4"
}

variable "search_index_name" {
  type        = string
  description = "Name of the Azure AI Search index used by the indexing/query pipeline"
  default     = "zenya-pdf-index"
}

variable "knowledge_source_name" {
  type        = string
  description = "Name of the Azure AI Search knowledge source"
  default     = "zenya-knowledgebase-source"
}

variable "knowledge_base_name" {
  type        = string
  description = "Name of the Azure AI Search knowledge base"
  default     = "zenya-knowledgebase"
}

variable "zenya_sync_ado_federated_credentials" {
  type = map(object({
    issuer  = string
    subject = string
  }))
  description = "Federated credentials on the Zenya sync identity (zenya_sync_identity.tf), keyed by credential name. One entry per ADO workload-identity service connection that must run as that identity; issuer and subject are copied verbatim from ADO's new-service-connection dialog. Empty = no ADO connection can act as the identity (Track B only)."
  default     = {}
}

variable "dev_allowed_ips" {
  type        = list(string)
  description = "Public IPs allowlisted for direct access to the function app, data storage account, and search service - development convenience only. Every usage site also gates on var.environment == \"development\", so this has no effect even if accidentally set in prod.tfvars."
  default     = []
}

variable "dev_developer_object_ids" {
  type        = list(string)
  description = "AAD object IDs of developers granted Search Index Data Reader on the dev search service, for manual knowledge-base querying/testing - development convenience only. See access.tf. Gated on var.environment == \"development\" like dev_allowed_ips."
  default     = []
}

variable "dev_eval_service_principal_object_id" {
  type        = string
  description = "Object ID of the service principal (con-cap-app-dev-spn) that runs the eval pipeline against dev, e.g. .pipelines/base/run-eval-tests.yml. Granted fixed role assignments in access.tf (azurerm_role_assignment.dev_eval_spn), independent of data.azurerm_client_config.current (azurerm_role_assignment.eval), so they don't shift if a human applies dev locally. Empty string disables these grants."
  default     = ""
}

variable "openai_mini_deployment" {
  type        = string
  description = "Deployment name for the completion model Content Understanding's prebuilt analyzers resolve against - runs gpt-5.4-mini since 2026-08-27 (ai_deployments.tf). Name frozen at \"gpt-4.1-mini\" for the same reason as openai_gpt_deployment. Never called by the app's own OpenAI code: ContentUnderstandingDefaultsSetup writes it into the account-wide default model->deployment mapping at host startup (app setting OPENAI_MINI_DEPLOYMENT, function_app.tf)."
  default     = "gpt-4.1-mini"
}
