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

variable "openai_sandbox_deployment" {
  type        = string
  description = "Deployment name for the sandbox project's own gpt-5.4-mini deployment (ai_sandbox.tf). Separate from the application's deployments, and on a model the app does not use, so neither can consume the other's quota."
  default     = "gpt-5.4-mini-sandbox"
}

variable "sandbox_deployment_capacity" {
  type        = number
  description = "Provisioned capacity in K TPM for the sandbox deployment (ai_sandbox.tf). This is the sandbox's hard throughput ceiling, so raise it deliberately, not reflexively. 50 is a small slice of the 1000 K TPM gpt-5.4-mini pool, which is otherwise unused - and since this is the only deployment on that model, it is also the effective cap on the pool's total consumption."
  default     = 50

  validation {
    condition     = var.sandbox_deployment_capacity > 0 && var.sandbox_deployment_capacity <= 100
    error_message = "sandbox_deployment_capacity must be between 1 and 100 K TPM. Above that the sandbox stops being a bounded slice of the account's gpt-5.4-mini quota; if the sandbox genuinely needs more, change this ceiling in a reviewed commit rather than in a .tfvars file."
  }
}

variable "sandbox_user_object_ids" {
  type        = list(string)
  description = "AAD object IDs granted Azure AI Developer and Cognitive Services OpenAI User on the sandbox Foundry project - and on nothing else. Accepts an Entra group's object ID as readily as a user's; a group is preferable, since membership changes then need no terraform apply. Gated on var.environment == \"development\" like dev_developer_object_ids. MUST NOT be reused for grants on the search service or data storage account - see ai_sandbox.tf for why that omission is the actual data boundary."
  default     = []
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

# ---------------------------------------------------------------------------
# Pipeline run report email - see communication.tf and
# docs/2608/260807/pipeline-run-email-report.md
# ---------------------------------------------------------------------------

variable "report_email_enabled" {
  type        = bool
  description = "Master switch for the per-run pipeline report email. Set false to mute local/dev runs - every StartIndexing sends mail otherwise. The function no-ops with an informational log when this is false."
  default     = true
}

variable "report_email_recipients" {
  type        = string
  description = "Semicolon-separated recipient list for the pipeline run report email. INTERNAL ADDRESSES ONLY: the mail body carries verbatim corpus excerpts (chunk samples) and attaches the full run summary as JSON - see pipeline-email-report-structure.md §5/§5a, where that exposure was accepted on the condition that recipients stay internal."
  default     = "gabriel.pirastru@devoteam.com"
}

variable "report_email_calibration_mode" {
  type        = bool
  description = "While true, thresholds with no defensible source yet (chunk coherence, size-band percentages, duplicate rate, cost multiplier) are rendered as observed values but raise no flag. Set false once ~2 weeks of runs have established real baselines. Thresholds that ARE sourced (index drift 15%, DocsFailed > 0, VectorDimErrors > 0, ReconciliationProblems > 0, oversized/undersized token bounds) flag regardless of this setting."
  default     = true
}

variable "report_email_sender_address" {
  type        = string
  description = "Explicit sender address for the pipeline report email, e.g. DoNotReply@<guid>.azurecomm.net. Leave empty to derive it from the Azure-managed domain's exported attribute (see function_app.tf) - set it explicitly only if that attribute reference fails at plan time."
  default     = ""
}
