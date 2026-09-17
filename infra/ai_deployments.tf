# OpenAI model deployments on the existing Foundry AI Services account (data.tf), looped - all
# GlobalStandard, only model and capacity vary. Model and quota facts: docs/ai-foundry-models.md
# (verified 2026-07-02, addenda 2026-08-27 and 2026-09-16).
#   - Deployment NAMES are frozen in variables.tf: renaming forces destroy+recreate, so
#     "gpt-4.1-query" runs gpt-5.4 and "gpt-4.1-mini" runs gpt-5.4-mini.
#   - Model versions differ per variant (gpt-5.4 = 2026-03-05, gpt-5.4-mini = 2026-03-17) - never
#     carry one across. A guessed version failed on 2026-08-27 with DeploymentModelNotSupported
#     AFTER the destroy had completed, taking CU down until the corrected entry applied
#     (docs/2608/260827/cu-completion-model-switch.md).
#   - A model change is ForceNew (destroy+recreate) regardless of the name.
#   - gpt-4.1 is blocked for new deployments (ServiceModelDeprecating); gpt-5.5 has 0 quota in this
#     sub/region; gpt-5.5-mini does not exist here.

locals {
  openai_deployments = {
    # 350 -> 1000 (2026-09-16): 1000 is the ENTIRE text-embedding-3-large GlobalStandard pool in
    # this sub/region (docs/ai-foundry-models.md: 350 / 1000 used/limit on the 2026-07-02 snapshot,
    # so the 650 headroom was free and no quota request is needed). Same rule as `mini` below:
    # nothing else may deploy text-embedding-3-large in this sub/region without taking capacity
    # from here. Three consumers draw from this one pool: chunk/identity embedding
    # (EmbeddingService, IdentityEmbedder), the index's query-time vectorizer
    # (IndexService.BuildVectorSearch) and Content Understanding via the account default
    # model->deployment mapping (function_app.tf). Headroom, not a fix for an observed 429: the
    # measured cold re-embeds (95-166 s embed step) ran at 0 retries against the old 350
    # (docs/2609/260916/vector-cache-performance.md section 4a).
    embedding = {
      name          = var.openai_embedding_deployment
      model_name    = "text-embedding-3-large"
      model_version = "1"
      capacity      = 1000
    }
    # 10 -> 200 (2026-07-30): RagEvaluationTests' Parallelize(Workers = 5) runs 5 query streams
    # concurrently and the 2026-07-30 eval run 429'd at 10. Matched to `evaluation`'s 200 - same
    # 5-worker concurrency, shouldn't be sized lower. Earned from real 429s; don't shrink it.
    # 200 -> 1000 (2026-09-16): the WHOLE gpt-5.4 pool, this deployment's alone. It used to share
    # it with `extraction` (200 + 500 = 700), deleted the same day because nothing consumed it: no
    # chat client was ever built against OPENAI_EXTRACTION_DEPLOYMENT, and Content Understanding
    # routes only through the account default mapping (gpt-5.4-mini -> `mini`,
    # text-embedding-3-large -> `embedding`), so the 2026-08-27 "CU bills figure analysis through
    # it" rationale for its 40 -> 500 was wrong (docs/2608/260827/extraction-coverage-chunking-
    # review.md). Nothing else may deploy gpt-5.4 in this sub/region from now on without taking
    # capacity from here. Headroom, not a fix for an observed 429.
    querying = {
      name          = var.openai_gpt_deployment
      model_name    = "gpt-5.4"
      model_version = "2026-03-05"
      capacity      = 1000
    }
    # Deliberately a different model from querying - avoids self-preference bias in
    # eval scores. 10 -> 50 (2026-07-29: ~5 judge calls per Answer test, ~3 per Refusal test, over
    # ~79 golden queries; the throttle delays in RagEvaluator/RefusalEvaluator were shortened to
    # match) -> 200 (2026-07-30: Parallelize(Workers = 3) added). gpt-5.1 pool is 1000 K TPM.
    # 200 -> 1000 (2026-09-16): the WHOLE gpt-5.1 pool. This is the only gpt-5.1 deployment in this
    # sub/region (docs/ai-foundry-models.md: 10 / 1000 used on 2026-07-29, the 10 being this one), so
    # nothing else may deploy gpt-5.1 here from now on without taking capacity from it. Headroom
    # for the parallel judge streams, not a fix for an observed 429.
    evaluation = {
      name          = var.openai_eval_deployment
      model_name    = "gpt-5.1"
      model_version = "2025-11-13"
      capacity      = 1000
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
