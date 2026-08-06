using Azure.Search.Documents;
using Microsoft.Extensions.DependencyInjection;
using AgenticRagApp.Querying.Guards;
using AgenticRagApp.Querying.Services;

namespace AgenticRagApp.Querying;

// All of querying's DI registrations live here, self-contained, so the Functions host
// (AgenticRagApp.FunctionApp/Program.cs) only ever needs one line — services.AddQuerying()
// — to wire it in. Assumes the host has already called AgenticRagApp.Infrastructure's
// AddAgenticRagAppInfrastructure() (SearchClient, IKnowledgeSourceStore, IKnowledgeService,
// IndexerConfig, IKnowledgeRetrievalClient, IPromptShieldClient, TextAnalyticsClient).
// Doc-type-agnostic — reads the one shared Search index regardless of which pipeline
// (PDF/CSV) wrote a given chunk.
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddQuerying(this IServiceCollection services)
    {
        services.AddSingleton(sp =>
            new ChunkNeighborExpander(sp.GetRequiredService<SearchClient>()));
        services.AddSingleton<IPromptInjectionGuard, PromptInjectionGuard>();
        services.AddSingleton<IPiiGuard, PiiGuard>();
        services.AddSingleton<IRagQueryService, AgenticRagQueryService>();

        return services;
    }
}
