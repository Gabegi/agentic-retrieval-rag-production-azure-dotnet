variable "environment" {
  type        = string
  description = "Environment name (development, production) - matches 1-infra-deploy.yml's envName. See naming.tf's env_short for the separate dev/prd shorthand baked into resource names."
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
  description = "Deployment name for the text-embedding-3-large model on the Foundry AI Services account"
  default     = "embedding-3-large"
}

variable "openai_gpt_deployment" {
  type        = string
  description = "Deployment name for the gpt-4.1 model used by the query API"
  default     = "gpt-4.1-query"
}

variable "openai_extraction_deployment" {
  type        = string
  description = "Deployment name for the gpt-4.1 model used by the indexing/extraction pipeline"
  default     = "gpt-4.1-extraction"
}

variable "openai_eval_deployment" {
  type        = string
  description = "Deployment name for the gpt-4o model used for evaluation"
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

variable "dev_allowed_ips" {
  type        = list(string)
  description = "Public IPs allowlisted for direct access to the function app, data storage account, and search service - development convenience only. Every usage site also gates on var.environment == \"development\", so this has no effect even if accidentally set in prod.tfvars."
  default     = []
}

variable "dev_developer_object_ids" {
  type        = list(string)
  description = "AAD object IDs of developers granted Search Index Data Reader on the dev search service, for manual knowledge-base querying/testing - development convenience only. See dev_access.tf. Gated on var.environment == \"development\" like dev_allowed_ips."
  default     = []
}

variable "dev_eval_service_principal_object_id" {
  type        = string
  description = "Object ID of the service principal (cor-cap-app-dev-spn) that runs the eval pipeline against dev, e.g. .pipelines/base/run-eval-tests.yml. Granted fixed role assignments in dev_access.tf, independent of data.azurerm_client_config.current (eval_access.tf), so they don't shift if a human applies dev locally. Empty string disables these grants."
  default     = ""
}
