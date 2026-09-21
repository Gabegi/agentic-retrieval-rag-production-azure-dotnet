using System.ComponentModel.DataAnnotations;

namespace AgenticRagApp.Infrastructure.Configuration;

public class IndexerConfig
{
    // ErrorMessage names each app-setting key (not the C# property) since that's what an
    // operator actually needs to go set in local.settings.json / the Function App config.
    [Required(ErrorMessage = "SEARCH_ENDPOINT is required")]               public string SearchEndpoint { get; init; } = default!;
    [Required(ErrorMessage = "OPENAI_ENDPOINT is required")]               public string OpenAiEndpoint { get; init; } = default!;
    [Required(ErrorMessage = "OPENAI_EMBEDDING_DEPLOYMENT is required")]   public string OpenAiEmbeddingDeployment { get; init; } = default!;
    [Required(ErrorMessage = "STORAGE_ACCOUNT_URL is required")]           public string StorageAccountUrl { get; init; } = default!;
    [Required(ErrorMessage = "SEARCH_INDEX_NAME is required")]             public string SearchIndexName { get; init; } = default!;
    [Required(ErrorMessage = "KNOWLEDGE_SOURCE_NAME is required")]         public string KnowledgeSourceName { get; init; } = default!;
    [Required(ErrorMessage = "KNOWLEDGE_BASE_NAME is required")]           public string KnowledgeBaseName { get; init; } = default!;
    [Required(ErrorMessage = "OPENAI_GPT_DEPLOYMENT is required")]         public string OpenAiGptDeployment { get; init; } = default!;
    [Required(ErrorMessage = "OPENAI_GPT_MODEL_NAME is required")]         public string OpenAiGptModelName { get; init; } = default!;
    // Required by AgenticRagQueryService's guard checks (acceptance criteria 4 & 5) -
    // unlike ContentUnderstandingEndpoint below, there is no code path that works without
    // these, so they can't be left optional/empty.
    [Required(ErrorMessage = "CONTENT_SAFETY_ENDPOINT is required")]      public string ContentSafetyEndpoint { get; init; } = default!;
    [Required(ErrorMessage = "LANGUAGE_ENDPOINT is required")]            public string LanguageEndpoint { get; init; } = default!;
    // Below all have a fallback applied at construction time (see ServiceCollectionExtensions),
    // so they're never actually null/empty in practice - not [Required].
    //
    // The source corpus lives on its own storage account since 2026-09-21 (D206), so it takes two
    // settings to name: this URL and StorageContainer below. Both are resolved in exactly one place
    // - the "source-documents" keyed BlobContainerClient in ServiceCollectionExtensions - and no
    // consumer names either of them again.
    //
    // Falls back to StorageAccountUrl when DOCUMENTS_STORAGE_ACCOUNT_URL is unset, so a host that
    // predates the split (local.settings.json, the eval harness, a single-account environment)
    // keeps working with one account and the split stays opt-in per environment.
    public string DocumentsStorageAccountUrl   { get; init; } = default!;
    // Was "protocols" until 2026-09-21, which was wrong in the only host that relied on the
    // default: function_app.tf never set STORAGE_CONTAINER, so the deployed Function App handed
    // RunReportAssembler a container terraform had never created, while the indexer read a
    // hardcoded "documents" beside it. Terraform now sets this explicitly; the default matches it
    // so the two can't drift apart again - if you change one, change the other.
    // "zenya-documents" (not "documents") since the same day: the indexed corpus is what the Zenya
    // sync mirrors. See function_app.tf for what that switch costs.
    public string StorageContainer             { get; init; } = "zenya-documents";
    // Optional here, but required by the indexing side - the Content Understanding client is
    // only registered when set, and AgenticRagApp.Indexing.CU's AddIndexing throws without
    // it. Optional at this level because the query-side host does no extraction and should not
    // need an extraction endpoint to start.
    //
    // Points at the shared Foundry AIServices account (infra/function_app.tf), because CU is a
    // data-plane capability on that account rather than a resource of its own. It carried the
    // same value as the now-removed DOCUMENT_INTELLIGENCE_ENDPOINT for exactly that reason, and
    // keeping them as two keys is what made retiring Document Intelligence a deletion here
    // rather than a re-plumbing.
    public string ContentUnderstandingEndpoint { get; init; } = "";
    // No analyzer-id or completion-model setting any more: the analyzer is the prebuilt
    // "prebuilt-documentSearch", hardcoded in ContentAnalysisClient. The models it needs resolve
    // through the account-wide default model->deployment mapping, which
    // ContentUnderstandingDefaultsSetup verifies at host startup (from this value, the two
    // below and OpenAiEmbeddingDeployment) and writes only when missing or wrong.
    public string OpenAiEmbeddingModelName     { get; init; } = "text-embedding-3-large";
    // The DEPLOYMENT Content Understanding's prebuilt analyzers resolve against
    // (infra/ai_deployments.tf "mini", serving gpt-5.4-mini since 2026-08-27). Consumed only by
    // ContentUnderstandingDefaultsSetup - the app's own OpenAI calls never touch it. Default
    // matches infra/variables.tf openai_mini_deployment, whose value is still the string
    // "gpt-4.1-mini" because it is a deployment name kept stable across the model change - do
    // not read it as the model name. That lives in
    // ContentUnderstandingDefaultsSetup.MiniModelName.
    public string OpenAiMiniDeployment         { get; init; } = "gpt-4.1-mini";
    public int    OpenAiEmbeddingDimensions    { get; init; } = 3072;

    // List price of the embedding model's INPUT tokens, in USD per 1M, used to turn the run's
    // measured token counts into the run report's cost figures (2026-09-15).
    //
    // Configuration and not a constant, deliberately: docs/report-schema.md refused to store
    // money in the report at all on the grounds that list price "changes without a code change".
    // That is exactly right, and it is an argument for a setting rather than for omission - a
    // price change is an app-setting edit here, not a redeploy. The rate is written into every
    // report next to the dollars it produced (EmbeddingCostMetrics.RateUsdPer1M), so a report
    // from six months ago still says what rate produced its number and stays comparable.
    //
    // Nothing reads this at query time and nothing bills from it: it is a reporting input only,
    // and a wrong value makes a report's dollars wrong while changing no behaviour.
    //
    // The default is text-embedding-3-large's standard-tier list rate. There is deliberately no
    // batch/standard switch: the indexing path calls GenerateAsync synchronously and submits no
    // batch jobs, so a "batch" label would describe something this code does not do. If the
    // pipeline ever moves to the batch tier, set this to that tier's rate.
    // Set to 0 to disable cost reporting entirely (the report's EmbeddingCost goes null).
    public decimal EmbeddingInputPriceUsdPer1MTokens { get; init; } = 0.130m;

    // Log-only mode for the query-time guards. Set true on 2026-08-12 by request, to be revisited
    // once eval shows how often each guard actually fires (docs/2608/260812/guards-review.md).
    //
    // true  = every guard in AgenticRagQueryService still runs and still logs, but no longer
    //         blocks. The user gets the knowledge base's answer regardless.
    // false = guards block, the original behaviour.
    //
    // While this is true the app does NOT enforce acceptance criteria 4 (prompt injection) or
    // 5 (no personal data in question or answer). Those criteria exist precisely because model
    // instructions can be bypassed, so nothing else covers them - see AcceptatieCriteria.md:41-45.
    //
    // Read from the GUARDS_LOG_ONLY app setting (ServiceCollectionExtensions, wired 2026-09-11;
    // set in infra/function_app.tf). Absent = true. Set it to "false" to restore enforcement -
    // no code change needed.
    public bool   GuardsLogOnly                { get; init; } = true;
}
