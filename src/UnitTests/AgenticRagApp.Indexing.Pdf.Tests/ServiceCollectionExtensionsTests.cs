using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using AgenticRagApp.Infrastructure.Clients.Embedding;
using AgenticRagApp.Infrastructure.Configuration;
using AgenticRagApp.Indexing.Pdf;
using AgenticRagApp.Indexing.Pdf.Services;
using AgenticRagApp.Indexing.Pdf.Utils;
using AgenticRagApp.Infrastructure.Clients.DocumentIdentity;
using AgenticRagApp.Observability.Reports;

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

        AssertSingleton<ITextSplitter, SectionSplitter>(services);
        AssertSingleton<SectionCascadeStrategy, SectionCascadeStrategy>(services);
        AssertSingleton<ChunkingStrategySelector, ChunkingStrategySelector>(services);
        AssertSingleton<IChunkingService, ChunkingService>(services);
        AssertSingleton<DocumentIdentityResolver, DocumentIdentityResolver>(services);

        // IDocumentIdentityStore is deliberately NOT registered here - it moved to
        // AddAgenticRagAppInfrastructure() with the other storage clients.
        Assert.IsFalse(services.Any(d => d.ServiceType == typeof(IDocumentIdentityStore)));
    }

    // The assertions above only inspect ServiceDescriptors, so they pass even when a
    // constructor takes something the container cannot supply (a bare string, say) - that
    // only surfaces the first time the graph is actually built, in the Functions host.
    // Resolving ChunkingService for real covers DocumentIdentityResolver and IDocumentIdentityStore
    // along with it.
    [TestMethod]
    public void AddPdfIndexing_ChunkingServiceGraph_IsResolvable()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Stands in for what AddAgenticRagAppInfrastructure() and the Functions host
        // contribute. No calls are made against these during resolution, so a bare endpoint
        // and a mock are enough. IPipelineArtifactWriter comes from the host (Program.cs) -
        // ChunkingService takes it because the chunking stage writes its own run report.
        services.AddSingleton(Config());
        services.AddSingleton(new BlobServiceClient(new Uri("https://storage.example.com")));
        services.AddSingleton(new Mock<IEmbeddingClient>().Object);
        services.AddSingleton(new Mock<IPipelineArtifactWriter>().Object);
        services.AddSingleton(new Mock<IDocumentIdentityStore>().Object);

        services.AddPdfIndexing(Config());

        using var provider = services.BuildServiceProvider(validateScopes: true);

        Assert.IsNotNull(provider.GetRequiredService<IChunkingService>());
        Assert.IsNotNull(provider.GetRequiredService<DocumentIdentityResolver>());
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
