using Azure.AI.ContentUnderstanding;
using AgenticRagApp.Infrastructure.Clients.ContentUnderstanding;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AgenticRagApp.Infrastructure;
using AgenticRagApp.Infrastructure.Configuration;

namespace RagApp.UnitTests.Infrastructure;

[TestClass]
public class ServiceCollectionExtensionsTests
{
    private static readonly Dictionary<string, string?> RequiredSettings = new()
    {
        ["SEARCH_ENDPOINT"]                          = "https://search.example.com",
        ["OPENAI_ENDPOINT"]                           = "https://openai.example.com",
        ["OPENAI_EMBEDDING_DEPLOYMENT"]               = "embedding-deployment",
        ["OPENAI_GPT_DEPLOYMENT"]                     = "gpt-deployment",
        ["OPENAI_GPT_MODEL_NAME"]                     = "gpt-4.1",
        ["STORAGE_ACCOUNT_URL"]                       = "https://storage.example.com",
        ["SEARCH_INDEX_NAME"]                         = "my-index",
        ["KNOWLEDGE_SOURCE_NAME"]                     = "my-knowledge-source",
        ["KNOWLEDGE_BASE_NAME"]                       = "my-knowledge-base",
        ["CONTENT_SAFETY_ENDPOINT"]                   = "https://content-safety.example.com",
        ["LANGUAGE_ENDPOINT"]                         = "https://language.example.com",
        ["APPLICATIONINSIGHTS_CONNECTION_STRING"]     = "InstrumentationKey=00000000-0000-0000-0000-000000000000",
        ["AzureWebJobsStorage:accountName"]            = "myaccount",
    };

    private static IConfiguration BuildConfiguration(Dictionary<string, string?>? overrides = null)
    {
        var settings = new Dictionary<string, string?>(RequiredSettings);
        if (overrides is not null)
            foreach (var (key, value) in overrides)
                settings[key] = value;

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    [TestMethod]
    public void AddAgenticRagAppInfrastructure_AllRequiredSettingsPresent_ReturnsPopulatedConfig()
    {
        var services      = new ServiceCollection();
        var configuration = BuildConfiguration();

        var config = services.AddAgenticRagAppInfrastructure(configuration);

        Assert.AreEqual("https://search.example.com", config.SearchEndpoint);
        Assert.AreEqual("https://openai.example.com", config.OpenAiEndpoint);
        Assert.AreEqual("embedding-deployment", config.OpenAiEmbeddingDeployment);
        Assert.AreEqual("gpt-deployment", config.OpenAiGptDeployment);
        Assert.AreEqual("gpt-4.1", config.OpenAiGptModelName);
        Assert.AreEqual("https://storage.example.com", config.StorageAccountUrl);
        Assert.AreEqual("my-index", config.SearchIndexName);
        Assert.AreEqual("my-knowledge-source", config.KnowledgeSourceName);
        Assert.AreEqual("my-knowledge-base", config.KnowledgeBaseName);
        Assert.AreEqual("https://content-safety.example.com", config.ContentSafetyEndpoint);
        Assert.AreEqual("https://language.example.com", config.LanguageEndpoint);
    }

    [TestMethod]
    public void AddAgenticRagAppInfrastructure_OptionalSettingsOmitted_FallsBackToDefaults()
    {
        var services      = new ServiceCollection();
        var configuration = BuildConfiguration();

        var config = services.AddAgenticRagAppInfrastructure(configuration);

        Assert.AreEqual("gpt-41-extraction", config.OpenAiExtractionDeployment);
        Assert.AreEqual("protocols", config.StorageContainer);
        Assert.AreEqual("text-embedding-3-large", config.OpenAiEmbeddingModelName);
        Assert.AreEqual(3072, config.OpenAiEmbeddingDimensions);
    }

    [TestMethod]
    public void AddAgenticRagAppInfrastructure_OptionalSettingsProvided_OverridesDefaults()
    {
        var services      = new ServiceCollection();
        var configuration = BuildConfiguration(new()
        {
            ["OPENAI_EXTRACTION_DEPLOYMENT"]  = "custom-extraction",
            ["STORAGE_CONTAINER"]              = "custom-container",
            ["OPENAI_EMBEDDING_MODEL_NAME"]    = "custom-embedding-model",
            ["OPENAI_EMBEDDING_DIMENSIONS"]    = "1536",
        });

        var config = services.AddAgenticRagAppInfrastructure(configuration);

        Assert.AreEqual("custom-extraction", config.OpenAiExtractionDeployment);
        Assert.AreEqual("custom-container", config.StorageContainer);
        Assert.AreEqual("custom-embedding-model", config.OpenAiEmbeddingModelName);
        Assert.AreEqual(1536, config.OpenAiEmbeddingDimensions);
    }

    [TestMethod]
    public void AddAgenticRagAppInfrastructure_InvalidEmbeddingDimensions_FallsBackToDefault()
    {
        var services      = new ServiceCollection();
        var configuration = BuildConfiguration(new() { ["OPENAI_EMBEDDING_DIMENSIONS"] = "not-a-number" });

        var config = services.AddAgenticRagAppInfrastructure(configuration);

        Assert.AreEqual(3072, config.OpenAiEmbeddingDimensions);
    }

    [TestMethod]
    public void AddAgenticRagAppInfrastructure_MissingRequiredSettings_ThrowsListingEachMissingKey()
    {
        var services      = new ServiceCollection();
        var configuration = BuildConfiguration(new()
        {
            ["SEARCH_ENDPOINT"] = null,
            ["OPENAI_ENDPOINT"] = null,
        });

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            services.AddAgenticRagAppInfrastructure(configuration));

        StringAssert.Contains(ex.Message, "SEARCH_ENDPOINT");
        StringAssert.Contains(ex.Message, "OPENAI_ENDPOINT");
    }

    [TestMethod]
    public void AddAgenticRagAppInfrastructure_MissingSingleSetting_ThrowsForThatSettingOnly()
    {
        var services      = new ServiceCollection();
        var configuration = BuildConfiguration(new() { ["SEARCH_INDEX_NAME"] = "" });

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            services.AddAgenticRagAppInfrastructure(configuration));

        StringAssert.Contains(ex.Message, "SEARCH_INDEX_NAME");
    }

    [TestMethod]
    public void AddAgenticRagAppInfrastructure_ContentUnderstandingEndpointNotConfigured_DoesNotRegisterAnalysisClient()
    {
        var services      = new ServiceCollection();
        var configuration = BuildConfiguration();

        services.AddAgenticRagAppInfrastructure(configuration);

        Assert.IsFalse(services.Any(d => d.ServiceType == typeof(ContentUnderstandingClient)));
        Assert.IsFalse(services.Any(d => d.ServiceType == typeof(IContentAnalysisClient)));
    }

    [TestMethod]
    public void AddAgenticRagAppInfrastructure_RegistersSharedIndexAndBlobServices()
    {
        var services      = new ServiceCollection();
        var configuration = BuildConfiguration();

        services.AddAgenticRagAppInfrastructure(configuration);

        Assert.IsTrue(services.Any(d => d.ServiceType.Name == "IBlobStore"));
        Assert.IsTrue(services.Any(d => d.ServiceType.Name == "IIndexService"));
        Assert.IsTrue(services.Any(d => d.ServiceType.Name == "IIndexDocumentService"));
        Assert.IsTrue(services.Any(d => d.ServiceType.Name == "IIndexRebuildService"));
    }

    [TestMethod]
    public void AddAgenticRagAppInfrastructure_ContentUnderstandingEndpointNotConfigured_DoesNotRegisterContentUnderstandingClient()
    {
        var services      = new ServiceCollection();
        var configuration = BuildConfiguration();

        services.AddAgenticRagAppInfrastructure(configuration);

        Assert.IsFalse(services.Any(d => d.ServiceType == typeof(ContentUnderstandingClient)));
        Assert.IsFalse(services.Any(d => d.ServiceType == typeof(IContentAnalysisClient)));
        Assert.IsFalse(services.Any(d => d.ServiceType == typeof(IContentUnderstandingVerifier)));
    }

    [TestMethod]
    public void AddAgenticRagAppInfrastructure_ContentUnderstandingEndpointConfigured_RegistersContentUnderstandingClient()
    {
        var services      = new ServiceCollection();
        var configuration = BuildConfiguration(new() { ["CONTENT_UNDERSTANDING_ENDPOINT"] = "https://cu.example.com" });

        services.AddAgenticRagAppInfrastructure(configuration);

        Assert.IsTrue(services.Any(d => d.ServiceType == typeof(ContentUnderstandingClient)));
        Assert.IsTrue(services.Any(d => d.ServiceType == typeof(IContentAnalysisClient)));
        Assert.IsTrue(services.Any(d => d.ServiceType == typeof(IContentUnderstandingVerifier)));
    }

    // CONTENT_UNDERSTANDING_ENDPOINT is the only extraction-backend setting now - the Document
    // Intelligence gate that used to sit beside it is gone with its client. The pair of tests
    // that kept the two gates keyed to their own settings went with it; what still matters is
    // that setting this one key lights up the whole CU client set, since the indexing side
    // resolves IContentAnalysisClient on the strength of the same value.
    [TestMethod]
    public void AddAgenticRagAppInfrastructure_ContentUnderstandingConfigured_RegistersTheWholeClientSet()
    {
        var services      = new ServiceCollection();
        var configuration = BuildConfiguration(new() { ["CONTENT_UNDERSTANDING_ENDPOINT"] = "https://shared.example.com" });

        services.AddAgenticRagAppInfrastructure(configuration);

        Assert.IsTrue(services.Any(d => d.ServiceType == typeof(ContentUnderstandingClient)));
        Assert.IsTrue(services.Any(d => d.ServiceType == typeof(IContentAnalysisClient)));
        Assert.IsTrue(services.Any(d => d.ServiceType == typeof(IContentUnderstandingVerifier)));
    }

    [TestMethod]
    public void AddAgenticRagAppInfrastructure_ContentUnderstandingDefaults_AreAppliedWhenKeysAbsent()
    {
        var services      = new ServiceCollection();
        var configuration = BuildConfiguration();

        var config = services.AddAgenticRagAppInfrastructure(configuration);

        Assert.AreEqual("", config.ContentUnderstandingEndpoint);
        Assert.AreEqual("cap-pdf-layout", config.ContentUnderstandingAnalyzerId);
        // A MODEL name, never a deployment name - ContentAnalyzer.Models takes the model and the
        // account defaults map it to the deployment. Must stay on CU's supported-model list.
        Assert.AreEqual("gpt-5.4", config.ContentUnderstandingCompletionModel);
    }
}
