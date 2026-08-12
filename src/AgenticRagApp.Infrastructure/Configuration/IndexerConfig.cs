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
    // unlike DocumentIntelligenceEndpoint below, there's no code path that works without
    // these, so they can't be left optional/empty.
    [Required(ErrorMessage = "CONTENT_SAFETY_ENDPOINT is required")]      public string ContentSafetyEndpoint { get; init; } = default!;
    [Required(ErrorMessage = "LANGUAGE_ENDPOINT is required")]            public string LanguageEndpoint { get; init; } = default!;
    // Below all have a fallback applied at construction time (see ServiceCollectionExtensions),
    // so they're never actually null/empty in practice - not [Required].
    public string StorageContainer             { get; init; } = "protocols";
    public string OpenAiExtractionDeployment    { get; init; } = "gpt-41-extraction";
    // Optional - PDF's Document Intelligence extraction backend is only registered when set.
    public string DocumentIntelligenceEndpoint { get; init; } = "";
    public string OpenAiEmbeddingModelName     { get; init; } = "text-embedding-3-large";
    public int    OpenAiEmbeddingDimensions    { get; init; } = 3072;

    // TEMPORARY - set true 2026-08-12 by request, to be revisited once eval shows how often
    // each guard actually fires (docs/2608/260812/guards-review.md).
    //
    // true  = every guard in AgenticRagQueryService still runs and still logs, but no longer
    //         blocks. The user gets the knowledge base's answer regardless.
    // false = guards block, the original behaviour.
    //
    // While this is true the app does NOT enforce acceptance criteria 4 (prompt injection) or
    // 5 (no personal data in question or answer). Those criteria exist precisely because model
    // instructions can be bypassed, so nothing else covers them - see AcceptatieCriteria.md:41-45.
    // Set GUARDS_LOG_ONLY=false to restore enforcement; no code change needed.
    public bool   GuardsLogOnly                { get; init; } = true;
}
