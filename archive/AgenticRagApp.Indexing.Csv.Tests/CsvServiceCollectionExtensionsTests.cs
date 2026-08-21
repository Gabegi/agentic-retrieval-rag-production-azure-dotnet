using Microsoft.Extensions.DependencyInjection;
using AgenticRagApp.Indexing.Csv;
using AgenticRagApp.Indexing.Csv.Services;

namespace RagApp.UnitTests.CsvExtraction;

[TestClass]
public class ServiceCollectionExtensionsTests
{
    [TestMethod]
    public void AddCsvIndexing_RegistersExtractionPipelineStagesAsSingletons()
    {
        var services = new ServiceCollection();

        services.AddCsvIndexing();

        AssertSingleton<ICsvExtractor, CsvExtractor>(services);
        AssertSingleton<ICsvJoiner, CsvJoiner>(services);
        AssertSingleton<IDataCleaner, DataCleaner>(services);
        AssertSingleton<IPipelineValidator, PipelineValidator>(services);
    }

    [TestMethod]
    public void AddCsvIndexing_RegistersChunkingServices()
    {
        var services = new ServiceCollection();

        services.AddCsvIndexing();

        AssertSingleton<ICsvChunkingStrategy, ChunkingStrategy1>(services);
        AssertSingleton<ICsvChunkingService, CsvChunkingService>(services);
    }

    [TestMethod]
    public void AddCsvIndexing_RegistersDiffEmbedUploadPipeline()
    {
        var services = new ServiceCollection();

        services.AddCsvIndexing();

        AssertSingleton<ICsvExtractionService, CsvExtractionService>(services);
        AssertSingleton<ICsvEmbeddingService, CsvEmbeddingService>(services);
        AssertSingleton<ICsvUploadService, CsvUploadService>(services);
    }

    [TestMethod]
    public void AddCsvIndexing_RegistersExtractionOrchestratorViaFactory()
    {
        var services = new ServiceCollection();

        services.AddCsvIndexing();

        var descriptor = services.Single(d => d.ServiceType == typeof(ICsvExtractionOrchestrator));
        Assert.AreEqual(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.IsNotNull(descriptor.ImplementationFactory);
    }

    [TestMethod]
    public void AddCsvIndexing_ReturnsSameServiceCollectionInstance_ForChaining()
    {
        var services = new ServiceCollection();

        var result = services.AddCsvIndexing();

        Assert.AreSame(services, result);
    }

    private static void AssertSingleton<TService, TImplementation>(IServiceCollection services)
    {
        var descriptor = services.Single(d => d.ServiceType == typeof(TService));
        Assert.AreEqual(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.AreEqual(typeof(TImplementation), descriptor.ImplementationType);
    }
}
