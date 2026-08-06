# ---------------------------------------------------------------------------
# OpenAI model deployments on the existing Foundry AI Services account
# (data.azurerm_cognitive_account.foundry, see data.tf). Model choices and
# quota verified 2026-07-02 against cor-cap-dev/westeurope - see
# docs/ai-foundry-models.md. gpt-4.1 is blocked for new deployments
# (ServiceModelDeprecating); gpt-5.4 is the newest GA flagship with quota
# actually available (gpt-5.5 exists but has 0 quota in this sub/region).
# ---------------------------------------------------------------------------

# Looped via for_each rather than one resource block each - only the model
# and capacity vary per deployment.

locals {
  openai_deployments = {
    embedding = {
      name          = var.openai_embedding_deployment
      model_name    = "text-embedding-3-large"
      model_version = "1"
      capacity      = 350
    }
    # Capacity raised 10->200 (2026-07-30): same root cause as the evaluation
    # deployment's 50->200 bump - RagEvaluationTests.cs's
    # [assembly: Parallelize(Workers = 5, ...)] means up to 5 test streams now
    # issue real RAG queries against this deployment concurrently, where 10
    # K TPM was sized for a single stream. Confirmed live: the 2026-07-30 eval
    # run 429'd on this deployment ("requests to gpt-5.4 for gpt-4.1-query in
    # westeurope have exceeded rate limit") partway through the golden-query
    # set. Matched to the evaluation deployment's 200 rather than a smaller
    # bump, since each Answer-scenario test issues one querying call plus up
    # to 5 judge calls against `evaluation` - if 200 is the right headroom for
    # the judge fan-out at 5 workers, querying shouldn't be sized any lower
    # for the same 5-worker concurrency. Quota confirmed available: gpt-5.4
    # GlobalStandard usage was ~0/1000 K TPM before this bump
    # (docs/ai-foundry-models.md), so 200 leaves ~800 K TPM free (shared with
    # the `extraction` deployment below, same model).
    querying = {
      name          = var.openai_gpt_deployment
      model_name    = "gpt-5.4"
      model_version = "2026-03-05"
      capacity      = 200
    }
    extraction = {
      name          = var.openai_extraction_deployment
      model_name    = "gpt-5.4"
      model_version = "2026-03-05"
      capacity      = 40
    }
    # Deliberately a different model/version from "querying"/"extraction"
    # (gpt-5.4) to avoid self-preference bias in eval scores.
    # Capacity raised 10->50 (2026-07-29): the eval suite runs ~5 sequential
    # judge calls per Answer-scenario test (Groundedness/Relevance/Coherence/
    // Equivalence/Retrieval) and ~3 per Refusal-scenario test across ~79
    # golden queries - at capacity 10 that volume needed the 2s/5s throttle
    # delays in RagEvaluator.cs/RefusalEvaluator.cs/RagEvaluationTests.cs to
    # avoid 429s. Shortened those delays to match - see those files.
    # Raised 50->200 (2026-07-30): RagEvaluationTests.cs added
    # [assembly: Parallelize(Workers = 3, ...)], so up to 3 test streams now
    # hit this deployment concurrently - the 500ms-delay tuning above assumed
    # a single stream. 200 gives each of the 3 workers roughly the same
    # per-stream TPM headroom as the single-stream tuning at 50, with margin.
    # Quota confirmed available: gpt-5.1 GlobalStandard usage was 10/1000 K
    # TPM before any of these bumps (docs/ai-foundry-models.md), so 200 still
    # leaves 800 K TPM free.
    evaluation = {
      name          = var.openai_eval_deployment
      model_name    = "gpt-5.1"
      model_version = "2025-11-13"
      capacity      = 200
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
