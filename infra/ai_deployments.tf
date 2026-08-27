# ---------------------------------------------------------------------------
# OpenAI model deployments on the existing Foundry AI Services account
# (data.azurerm_cognitive_account.foundry, see data.tf). Model choices and
# quota verified 2026-07-02 against con-cap-dev/westeurope - see
# docs/ai-foundry-models.md. gpt-4.1 is blocked for new deployments
# (ServiceModelDeprecating); gpt-5.4 is the newest GA flagship with quota
# actually available (gpt-5.5 exists but has 0 quota in this sub/region).
#
# The `mini` entry (Content Understanding) moved to gpt-5.4-mini on 2026-08-27; its
# model/version were verified that day against a fresh list-models run (see the entry itself
# and docs/ai-foundry-models.md's 2026-08-27 addendum).
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
    # (docs/ai-foundry-models.md), so 200 left ~800 K TPM free (shared with
    # the `extraction` deployment below, same model; extraction's 2026-08-27
    # raise to 500 brings the pool to 700/1000 - ~300 K TPM free).
    querying = {
      name          = var.openai_gpt_deployment
      model_name    = "gpt-5.4"
      model_version = "2026-03-05"
      capacity      = 200
    }
    # Capacity 40 -> 500 (2026-08-27): the 40 predated Content Understanding and had no
    # recorded sizing rationale. CU's figure analysis now bills its per-figure LLM tokens
    # through this deployment (docs/2608/260819/content-understanding-when-and-how.md:
    # ~1,200 tokens per figure), at the same 8-document parallelism
    # (ExtractionService.MaxExtractionParallelism) that TPM-bound the `mini` deployment on
    # 2026-08-25 - so 40 K TPM had the same crawl risk on figure-heavy corpora. 500 leaves
    # 300 K TPM free in the shared gpt-5.4 pool (1000 total, `querying` holds 200 - a value
    # earned from real 429s, don't shrink it to feed this one).
    extraction = {
      name          = var.openai_extraction_deployment
      model_name    = "gpt-5.4"
      model_version = "2026-03-05"
      capacity      = 500
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
    # The completion model Content Understanding's prebuilt analyzers resolve against -
    # prebuilt-documentSearch included, which is what ContentAnalysisClient submits. The app
    # never calls this deployment itself: CU resolves it through the account's default
    # model->deployment mapping, which ContentUnderstandingDefaultsSetup verifies (and fixes if
    # wrong) at host startup - see content_understanding.tf. The mapping can only point at a
    # deployment that exists, so this apply is the hard prerequisite.
    #
    # gpt-4.1-mini -> gpt-5.4-mini (2026-08-27). CU used to *require* gpt-4.1-mini; Microsoft
    # has since expanded analyzer support to the GPT-5 series (gpt-5 through gpt-5.5, in
    # standard/mini/nano variants), so the model is now a choice rather than a constraint. Taken
    # at the mini tier, like-for-like with what it replaces: prebuilt-documentSearch
    # contextualizes EVERY page of every document, so this is a high-volume path where the mini
    # economics matter more than flagship accuracy. This also clears the shelf-life problem the
    # previous comment flagged - gpt-4.1-mini retires 2026-10-14 (docs/ai-foundry-models.md).
    #
    # Why gpt-5.4-mini and not gpt-5.5-mini: gpt-5.5-mini DOES NOT EXIST in this account/region.
    # A first attempt targeted it with a guessed version ("2026-04-24", the gpt-5.5 flagship's)
    # and the 2026-08-27 apply failed with DeploymentModelNotSupported - after the destroy had
    # already completed, which took CU down until this corrected entry applied (see
    # docs/2608/260827/cu-completion-model-gpt-5-5-mini.md). A fresh list-models run that day
    # showed the newest minis in westeurope are gpt-5.4-mini/gpt-5.4-nano ("2026-03-17" - note
    # NOT the gpt-5.4 flagship's "2026-03-05"; versions differ per variant, never carry one
    # across). Both values below are verified against that run.
    #
    # The DEPLOYMENT name is deliberately still var.openai_mini_deployment ("gpt-4.1-mini") and
    # not renamed: renaming forces a destroy+create of the deployment, and this repo already
    # runs gpt-5.4 on deployments named "gpt-4.1-query"/"gpt-4.1-extraction" for the same
    # reason. Deployment names are stable here; models move under them. (A model change is
    # ForceNew and recreates the deployment anyway - that is how the failed apply orphaned it -
    # but keeping the name stable at least avoids a second rename-only recreate.)
    #
    # Capacity 5000 -> 1000: the old value was the whole gpt-4.1-mini pool, which this
    # deployment consumed alone. The gpt-5.4-mini GlobalStandard pool is 1000 K TPM total, and
    # 1000 takes ALL of it - the hard ceiling; anything higher needs an Azure quota request
    # (portal), not a bigger number here. Consequences, accepted deliberately on 2026-08-27:
    #   - Extraction throughput drops vs 5000. Context: the 51-doc corpus crawled at 50 K TPM
    #     (the 2026-08-25 finding); 1000 is still 20x that tuning point, so this bounds
    #     worst-case full-corpus runs, not normal operation.
    #   - This entry is the pool's only consumer: the sandbox model deployment that also
    #     targeted gpt-5.4-mini was deleted outright the same day, partly because this entry
    #     left it no quota to enable into (ai_sandbox.tf's banner). Anything new deployed onto
    #     gpt-5.4-mini in this sub/region has to take capacity from here first.
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
