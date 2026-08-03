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
    // Below all have a fallback applied at construction time (see ServiceCollectionExtensions),
    // so they're never actually null/empty in practice - not [Required].
    public string StorageContainer             { get; init; } = "protocols";
    public string OpenAiExtractionDeployment    { get; init; } = "gpt-41-extraction";
    // Optional - PDF's Document Intelligence extraction backend is only registered when set.
    public string DocumentIntelligenceEndpoint { get; init; } = "";
    public string OpenAiEmbeddingModelName     { get; init; } = "text-embedding-3-large";
    public int    OpenAiEmbeddingDimensions    { get; init; } = 3072;
}
