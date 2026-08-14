using System.ComponentModel.DataAnnotations;
using Azure.AI.DocumentIntelligence;
using Azure.AI.OpenAI;
using Azure.AI.TextAnalytics;
using Azure.Core;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.KnowledgeBases;
using Azure.Storage.Blobs;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AgenticRagApp.Infrastructure.Configuration;
using AgenticRagApp.Infrastructure.Clients.Blob;
using AgenticRagApp.Infrastructure.Clients.Search;
using AgenticRagApp.Infrastructure.Clients.KnowledgeRetrieval;
using AgenticRagApp.Infrastructure.Clients.DocumentIntelligence;
using AgenticRagApp.Infrastructure.Clients.DocumentIdentity;
using AgenticRagApp.Infrastructure.Clients.Embedding;
using AgenticRagApp.Infrastructure.Clients.ContentSafety;

namespace AgenticRagApp.Infrastructure;

public static class ServiceCollectionExtensions
{
    // Registers every Azure SDK client this app talks to, once, as singletons (all are
    // thread-safe by design) — the single source of truth other projects inject from,
    // rather than each constructing its own copy from config + credential.
    //
    // Returns the built IndexerConfig so the host can still branch on it for its own
    // conditional registrations (e.g. PDF extraction backend), without re-reading
    // configuration a second time.
    public static IndexerConfig AddAgenticRagAppInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Fail fast with a named list of missing settings, rather than letting a missing
        // value surface later as an obscure NullReferenceException or UriFormatException.
        // Two keys below (APPLICATIONINSIGHTS_CONNECTION_STRING, AzureWebJobsStorage:accountName)
        // are Azure Functions host settings, not IndexerConfig fields, so they're checked
        // here rather than via [Required] on the config object below.
        var requiredHostKeys = new[]
        {
            "APPLICATIONINSIGHTS_CONNECTION_STRING",
            "AzureWebJobsStorage:accountName",
        };
        var missingHostKeys = requiredHostKeys.Where(k => string.IsNullOrWhiteSpace(configuration[k])).ToList();
        if (missingHostKeys.Count > 0)
            throw new InvalidOperationException(
                $"Missing required app setting(s): {string.Join(", ", missingHostKeys)}. " +
                "Set these in local.settings.json (local) or the Function App configuration (deployed).");

        var config = new IndexerConfig
        {
            SearchEndpoint               = configuration["SEARCH_ENDPOINT"]!,
            OpenAiEndpoint               = configuration["OPENAI_ENDPOINT"]!,
            OpenAiEmbeddingDeployment    = configuration["OPENAI_EMBEDDING_DEPLOYMENT"]!,
            OpenAiGptDeployment          = configuration["OPENAI_GPT_DEPLOYMENT"]!,
            OpenAiGptModelName           = configuration["OPENAI_GPT_MODEL_NAME"]!,
            OpenAiExtractionDeployment   = configuration["OPENAI_EXTRACTION_DEPLOYMENT"] ?? "gpt-41-extraction",
            DocumentIntelligenceEndpoint = configuration["DOCUMENT_INTELLIGENCE_ENDPOINT"] ?? "",
            ContentSafetyEndpoint        = configuration["CONTENT_SAFETY_ENDPOINT"]!,
            LanguageEndpoint             = configuration["LANGUAGE_ENDPOINT"]!,
            StorageAccountUrl            = configuration["STORAGE_ACCOUNT_URL"]!,
            StorageContainer             = configuration["STORAGE_CONTAINER"] ?? "protocols",
            SearchIndexName              = configuration["SEARCH_INDEX_NAME"]!,
            KnowledgeSourceName          = configuration["KNOWLEDGE_SOURCE_NAME"]!,
            KnowledgeBaseName            = configuration["KNOWLEDGE_BASE_NAME"]!,
            OpenAiEmbeddingModelName     = configuration["OPENAI_EMBEDDING_MODEL_NAME"] ?? "text-embedding-3-large",
            OpenAiEmbeddingDimensions    = int.TryParse(configuration["OPENAI_EMBEDDING_DIMENSIONS"], out var dims) ? dims : 3072,
        };

        // [Required]-annotated IndexerConfig properties, validated here rather than via the
        // Options pattern's ValidateOnStart: config keys above are SCREAMING_SNAKE_CASE and
        // don't match IndexerConfig's property names, so IConfiguration.Bind() can't
        // populate this type directly - it's assembled manually above instead. Validating
        // the finished object still catches every required field regardless of which of
        // the two constructors (here, or RagEvaluationTests) built it, with the same
        // "named list of missing settings" fail-fast rather than a null surfacing later
        // as an obscure NullReferenceException or UriFormatException.
        var validationResults = new List<ValidationResult>();
        if (!Validator.TryValidateObject(config, new ValidationContext(config), validationResults, validateAllProperties: true))
            throw new InvalidOperationException(
                $"Invalid IndexerConfig: {string.Join(", ", validationResults.Select(r => r.ErrorMessage))}. " +
                "Set these in local.settings.json (local) or the Function App configuration (deployed).");

        TokenCredential credential = new DefaultAzureCredential();

        services.AddSingleton(config);
        services.AddSingleton(credential);

        services.AddSingleton(_ =>
            new BlobServiceClient(new Uri(config.StorageAccountUrl), credential));

        // Pipeline temp storage — passes large payloads between Durable activities via blob
        // rather than through Durable Table Storage (64KB row-size limit).
        services.AddKeyedSingleton<BlobContainerClient>("pipeline-temp", (_, _) =>
        {
            var accountName = configuration["AzureWebJobsStorage:accountName"]!;
            return new BlobServiceClient(
                new Uri($"https://{accountName}.blob.core.windows.net"),
                credential)
                .GetBlobContainerClient("indexing-pipeline");
        });

        services.AddSingleton(_ =>
            new AzureOpenAIClient(new Uri(config.OpenAiEndpoint), credential));

        services.AddEmbeddingGenerator(sp =>
            sp.GetRequiredService<AzureOpenAIClient>()
              .GetEmbeddingClient(config.OpenAiEmbeddingDeployment)
              .AsIEmbeddingGenerator())
            .UseOpenTelemetry(sourceName: "Microsoft.Extensions.AI", configure: c => c.EnableSensitiveData = true);

        services.AddChatClient(sp =>
            sp.GetRequiredService<AzureOpenAIClient>()
              .GetChatClient(config.OpenAiGptDeployment)
              .AsIChatClient())
            .UseOpenTelemetry(sourceName: "Microsoft.Extensions.AI", configure: c => c.EnableSensitiveData = true);

        // All three pin the api-version explicitly - see SearchServiceVersion for why the SDK
        // default is not good enough here.
        services.AddSingleton(_ =>
            new SearchClient(new Uri(config.SearchEndpoint), config.SearchIndexName, credential, SearchServiceVersion.Options()));
        services.AddSingleton(_ =>
            new SearchIndexClient(new Uri(config.SearchEndpoint), credential, SearchServiceVersion.Options()));
        services.AddSingleton(_ =>
            new KnowledgeBaseRetrievalClient(new Uri(config.SearchEndpoint), config.KnowledgeBaseName, credential, SearchServiceVersion.Options()));

        // Document Intelligence is optional — only registered when configured. Consumers
        // that need it (e.g. the PDF extraction backend) are responsible for checking
        // config.DocumentIntelligenceEndpoint themselves before resolving it.
        if (!string.IsNullOrWhiteSpace(config.DocumentIntelligenceEndpoint))
        {
            services.AddSingleton(_ =>
                new DocumentIntelligenceClient(new Uri(config.DocumentIntelligenceEndpoint), credential));
            services.AddSingleton<IDocumentAnalysisClient, DocumentAnalysisClient>();
        }

        // Prompt Shields has no .NET SDK wrapper (see PromptShieldClient's comment), so this
        // is a typed HttpClient instead of an Azure SDK client registration like the others
        // here. Unlike Document Intelligence above, this one isn't optional - Querying's
        // AgenticRagQueryService requires IPromptInjectionGuard unconditionally.
        services.AddHttpClient<IPromptShieldClient, PromptShieldClient>(client =>
            client.BaseAddress = new Uri(config.ContentSafetyEndpoint));

        services.AddSingleton(_ =>
            new TextAnalyticsClient(new Uri(config.LanguageEndpoint), credential));

        // Generic wrappers — every raw client above is only ever consumed through one of
        // these from here on. No caller outside this project holds a raw SDK client.
        services.AddSingleton<IBlobStore, BlobStore>();
        services.AddSingleton<IKnowledgeRetrievalClient, KnowledgeBaseClient>();
        services.AddSingleton<IEmbeddingClient, EmbeddingClient>();

        // Corpus-wide family/domain identity store — "pipeline-artifacts" container, under its
        // own document-identity/ path prefix (see DocumentIdentityStore), the same container
        // Indexing.Pdf's VectorCache uses. Consumed by DocumentIdentityResolver over there;
        // registered here because it is a storage client holding a raw BlobContainerClient.
        services.AddSingleton<IDocumentIdentityStore>(sp =>
            new DocumentIdentityStore(
                sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient("pipeline-artifacts")));

        // Shared Search index lifecycle + document CRUD — one instance for both PDF and
        // CSV, since both write into the same index (see IndexService's own comment).
        services.AddSingleton<IIndexService, IndexService>();
        services.AddSingleton<IIndexDocumentService, IndexDocumentService>();
        services.AddSingleton<IKnowledgeService, KnowledgeService>();
        // Composes the two above - owns the order they must be torn down and rebuilt in.
        services.AddSingleton<IIndexRebuildService, IndexRebuildService>();

        return config;
    }
}
