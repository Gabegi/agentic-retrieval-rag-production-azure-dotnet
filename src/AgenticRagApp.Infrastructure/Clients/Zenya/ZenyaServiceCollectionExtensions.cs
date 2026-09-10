using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace AgenticRagApp.Infrastructure.Clients.Zenya;

// Separate from AddAgenticRagAppInfrastructure on purpose: only the sync hosts (the ZenyaSync
// console tool, later the sync Function App) talk to Zenya, and the query/indexing Function App
// must keep starting without ZENYA_* settings.
public static class ZenyaServiceCollectionExtensions
{
    public const string HttpClientName = "zenya";

    public static IServiceCollection AddZenyaClient(this IServiceCollection services, IConfiguration configuration)
    {
        var options = ZenyaOptions.FromConfiguration(configuration);
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);

        // One named client for both the token provider and the API client: same base address,
        // same 429 handler. The handler is resolved per client build so it can take the
        // options + logger from DI.
        services.AddTransient<ZenyaRetryHandler>();
        services.AddHttpClient(HttpClientName, client => client.BaseAddress = options.BaseUrl)
                .AddHttpMessageHandler<ZenyaRetryHandler>();

        // Singletons, not typed transient clients: the token provider IS the cache, so it must
        // outlive individual requests. The HttpClient each holds comes from the factory once;
        // handler rotation is lost for that instance, which is acceptable for hosts that run
        // minutes (the sync) and is the trade-off made knowingly here.
        if (options.UsesClientAssertion)
        {
            // Same DefaultAzureCredential the rest of the app uses (registered by
            // AddAgenticRagAppInfrastructure when that is also called); TryAdd so a host that
            // only syncs still gets one. Resolves to AzureCliCredential under an ADO service
            // connection and ManagedIdentityCredential on the Function App - which is the whole
            // point of the route: the identity comes from where the code runs.
            services.TryAddSingleton<TokenCredential>(_ => new DefaultAzureCredential());
            services.AddSingleton<IZenyaTokenProvider>(sp =>
                new ZenyaClientAssertionTokenProvider(
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName),
                    options,
                    sp.GetRequiredService<TokenCredential>(),
                    sp.GetRequiredService<ILogger<ZenyaClientAssertionTokenProvider>>(),
                    sp.GetRequiredService<TimeProvider>()));
        }
        else
        {
            services.AddSingleton<IZenyaTokenProvider>(sp =>
                new ZenyaClientSecretTokenProvider(
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName),
                    options,
                    sp.GetRequiredService<ILogger<ZenyaClientSecretTokenProvider>>(),
                    sp.GetRequiredService<TimeProvider>()));
        }

        services.AddSingleton<IZenyaClient>(sp =>
            new ZenyaClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName),
                sp.GetRequiredService<IZenyaTokenProvider>(),
                sp.GetRequiredService<ILogger<ZenyaClient>>()));

        return services;
    }
}
