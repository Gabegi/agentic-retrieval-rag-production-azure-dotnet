using Microsoft.Extensions.DependencyInjection;
using AgenticRagApp.Infrastructure.Configuration;
using AgenticRagApp.Indexing.Pdf;
using AgenticRagApp.Indexing.Pdf.Services;

namespace RagApp.UnitTests.PdfExtraction;

[TestClass]
public class ServiceCollectionExtensionsTests
{
    private static IndexerConfig Config(string documentIntelligenceEndpoint = "") => new()
    {
        SearchEndpoint            = "https://search.example.com",
        OpenAiEndpoint            = "https://openai.example.com",
        OpenAiEmbeddingDeployment = "embedding-deployment",
        OpenAiGptDeployment       = "gpt-deployment",
        OpenAiGptModelName        = "gpt-4.1",
        StorageAccountUrl         = "https://storage.example.com",
        StorageContainer          = "protocols",
        SearchIndexName           = "my-index",
        KnowledgeSourceName       = "my-knowledge-source",
        KnowledgeBaseName         = "my-knowledge-base",
        DocumentIntelligenceEndpoint = documentIntelligenceEndpoint,
    };

    [TestMethod]
    public void AddPdfIndexing_DocumentIntelligenceNotConfigured_DoesNotRegisterExtractorOrAnalyzer()
    {
        var services = new ServiceCollection();

        services.AddPdfIndexing(Config());

        Assert.IsFalse(services.Any(d => d.ServiceType == typeof(IPdfExtractor)));
        Assert.IsFalse(services.Any(d => d.ServiceType == typeof(PdfDocumentIntelligenceAnalyzer)));
    }

    [TestMethod]
    public void AddPdfIndexing_DocumentIntelligenceConfigured_RegistersExtractorAndAnalyzer()
    {
        var services = new ServiceCollection();

        services.AddPdfIndexing(Config(documentIntelligenceEndpoint: "https://di.example.com"));

        var extractorDescriptor = services.Single(d => d.ServiceType == typeof(IPdfExtractor));
        Assert.AreEqual(typeof(PdfExtractor), extractorDescriptor.ImplementationType);
        Assert.IsTrue(services.Any(d => d.ServiceType == typeof(PdfDocumentIntelligenceAnalyzer)));
    }

    [TestMethod]
    public void AddPdfIndexing_RegistersChunkingServices()
    {
        var services = new ServiceCollection();

        services.AddPdfIndexing(Config());

        AssertSingleton<IChunkingStrategy, PdfChunkingStrategy2>(services);
        AssertSingleton<IChunkingService, ChunkingService>(services);
    }

    [TestMethod]
    public void AddPdfIndexing_RegistersCleaningAndValidationServices()
    {
        var services = new ServiceCollection();

        services.AddPdfIndexing(Config());

        AssertSingleton<IPdfCleaner, PdfCleaner>(services);
        AssertSingleton<IPdfPipelineValidator, PdfPipelineValidator>(services);
    }

    [TestMethod]
    public void AddPdfIndexing_RegistersDiffEmbedUploadAndRecoveryPipeline()
    {
        var services = new ServiceCollection();

        services.AddPdfIndexing(Config());

        Assert.IsTrue(services.Any(d => d.ServiceType == typeof(IExtractionService)));
        AssertSingleton<IEmbeddingService, EmbeddingService>(services);
        AssertSingleton<IUploadService, UploadService>(services);
        Assert.IsTrue(services.Any(d => d.ServiceType == typeof(IVectorCache)));
        AssertSingleton<IRestoreService, RestoreService>(services);
    }

    [TestMethod]
    public void AddPdfIndexing_RegistersExtractionOrchestratorViaFactory()
    {
        var services = new ServiceCollection();

        services.AddPdfIndexing(Config());

        var descriptor = services.Single(d => d.ServiceType == typeof(IExtractionOrchestrator));
        Assert.AreEqual(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.IsNotNull(descriptor.ImplementationFactory);
    }

    [TestMethod]
    public void AddPdfIndexing_ReturnsSameServiceCollectionInstance_ForChaining()
    {
        var services = new ServiceCollection();

        var result = services.AddPdfIndexing(Config());

        Assert.AreSame(services, result);
    }

    private static void AssertSingleton<TService, TImplementation>(IServiceCollection services)
    {
        var descriptor = services.Single(d => d.ServiceType == typeof(TService));
        Assert.AreEqual(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.AreEqual(typeof(TImplementation), descriptor.ImplementationType);
    }
}
