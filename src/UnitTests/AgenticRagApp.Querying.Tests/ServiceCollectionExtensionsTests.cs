using Microsoft.Extensions.DependencyInjection;
using AgenticRagApp.Querying;
using AgenticRagApp.Querying.Services;

namespace RagApp.UnitTests.Querying;

[TestClass]
public class ServiceCollectionExtensionsTests
{
    [TestMethod]
    public void AddQuerying_RegistersChunkNeighborExpanderViaFactory()
    {
        var services = new ServiceCollection();

        services.AddQuerying();

        var descriptor = services.Single(d => d.ServiceType == typeof(ChunkNeighborExpander));
        Assert.AreEqual(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.IsNotNull(descriptor.ImplementationFactory);
    }

    [TestMethod]
    public void AddQuerying_RegistersRagQueryServiceAsSingleton()
    {
        var services = new ServiceCollection();

        services.AddQuerying();

        AssertSingleton<IRagQueryService, AgenticRagQueryService>(services);
    }

    [TestMethod]
    public void AddQuerying_RegistersKnowledgeServiceAsSingleton()
    {
        var services = new ServiceCollection();

        services.AddQuerying();

        AssertSingleton<IKnowledgeService, KnowledgeService>(services);
    }

    [TestMethod]
    public void AddQuerying_ReturnsSameServiceCollectionInstance_ForChaining()
    {
        var services = new ServiceCollection();

        var result = services.AddQuerying();

        Assert.AreSame(services, result);
    }

    private static void AssertSingleton<TService, TImplementation>(IServiceCollection services)
    {
        var descriptor = services.Single(d => d.ServiceType == typeof(TService));
        Assert.AreEqual(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.AreEqual(typeof(TImplementation), descriptor.ImplementationType);
    }
}
