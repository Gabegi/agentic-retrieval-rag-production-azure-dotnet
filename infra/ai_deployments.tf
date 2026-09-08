# OpenAI model deployments on the existing Foundry AI Services account (data.tf), looped - all
# GlobalStandard, only model and capacity vary. Model and quota facts: docs/ai-foundry-models.md
# (verified 2026-07-02, addendum 2026-08-27).
#   - Deployment NAMES are frozen in variables.tf: renaming forces destroy+recreate, so
#     "gpt-4.1-query"/"gpt-4.1-extraction" run gpt-5.4 and "gpt-4.1-mini" runs gpt-5.4-mini.
#   - Model versions differ per variant (gpt-5.4 = 2026-03-05, gpt-5.4-mini = 2026-03-17) - never
#     carry one across. A guessed version failed on 2026-08-27 with DeploymentModelNotSupported
#     AFTER the destroy had completed, taking CU down until the corrected entry applied
#     (docs/2608/260827/cu-completion-model-switch.md).
#   - A model change is ForceNew (destroy+recreate) regardless of the name.
#   - gpt-4.1 is blocked for new deployments (ServiceModelDeprecating); gpt-5.5 has 0 quota in this
#     sub/region; gpt-5.5-mini does not exist here.

locals {
  openai_deployments = {
    embedding = {
      name          = var.openai_embedding_deployment
      model_name    = "text-embedding-3-large"
      model_version = "1"
      capacity      = 350
    }
    # 10 -> 200 (2026-07-30): RagEvaluationTests' Parallelize(Workers = 5) runs 5 query streams
    # concurrently and the 2026-07-30 eval run 429'd at 10. Matched to `evaluation`'s 200 - same
    # 5-worker concurrency, shouldn't be sized lower. Earned from real 429s; don't shrink it.
    # Shares the 1000 K TPM gpt-5.4 pool with `extraction` (200 + 500 = 700 used).
    querying = {
      name          = var.openai_gpt_deployment
      model_name    = "gpt-5.4"
      model_version = "2026-03-05"
      capacity      = 200
    }
    # 40 -> 500 (2026-08-27): the 40 predated CU and had no recorded rationale. CU's figure
    # analysis bills ~1,200 tokens/figure through this deployment
    # (docs/2608/260819/content-understanding-when-and-how.md) at the same 8-document parallelism
    # (ExtractionService.MaxExtractionParallelism) that TPM-bound `mini` on 2026-08-25.
    extraction = {
      name          = var.openai_extraction_deployment
      model_name    = "gpt-5.4"
      model_version = "2026-03-05"
      capacity      = 500
    }
    # Deliberately a different model from querying/extraction - avoids self-preference bias in
    # eval scores. 10 -> 50 (2026-07-29: ~5 judge calls per Answer test, ~3 per Refusal test, over
    # ~79 golden queries; the throttle delays in RagEvaluator/RefusalEvaluator were shortened to
    # match) -> 200 (2026-07-30: Parallelize(Workers = 3) added). gpt-5.1 pool is 1000 K TPM.
    evaluation = {
      name          = var.openai_eval_deployment
      model_name    = "gpt-5.1"
      model_version = "2025-11-13"
      capacity      = 200
    }
    # Content Understanding's completion model for prebuilt-documentSearch. The app never calls
    # it; CU resolves it through the account default model->deployment mapping
    # (function_app.tf), which can only point at an existing deployment - so this apply
    # is the hard prerequisite.
    #   - gpt-4.1-mini -> gpt-5.4-mini (2026-08-27): CU used to require gpt-4.1-mini, now supports
    #     the GPT-5 series. Mini tier because documentSearch contextualizes EVERY page - volume
    #     economics over flagship accuracy. Also clears gpt-4.1-mini's 2026-10-14 retirement.
    #   - Capacity 5000 -> 1000: 1000 is the ENTIRE gpt-5.4-mini GlobalStandard pool; more needs an
    #     Azure quota request. Throughput drops vs 5000 but is still 20x the 50 K TPM at which the
    #     51-doc corpus crawled on 2026-08-25. Nothing else may deploy onto gpt-5.4-mini in this
    #     sub/region without taking capacity from here (the sandbox deployment was deleted for
    #     exactly that reason on the same day; recover it from git history if ever needed).
    mini = {
      name          = var.openai_mini_deployment
      model_name    = "gpt-5.4-mini"
      model_version = "2026-03-17"
      capacity      = 1000
    }
  }
}

resource "azurerm_cognitive_deployment" "openai" {
  for_each             = local.openai_deployments
  name                 = each.value.name
  cognitive_account_id = data.azurerm_cognitive_account.foundry.id

  model {
    format  = "OpenAI"
    name    = each.value.model_name
    version = each.value.model_version
  }

  sku {
    name     = "GlobalStandard"
    capacity = each.value.capacity
  }
}
