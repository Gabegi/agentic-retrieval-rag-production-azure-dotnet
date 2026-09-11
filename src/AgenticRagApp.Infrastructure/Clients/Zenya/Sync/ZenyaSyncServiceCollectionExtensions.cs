using AgenticRagApp.Infrastructure.Clients.Blob;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgenticRagApp.Infrastructure.Clients.Zenya.Sync;

// Registers the sync on top of AddZenyaClient. Separate from AddAgenticRagAppInfrastructure for
// the same reason the client is: only the sync hosts (Tools.ZenyaSync today, a timer function
// if Track B ever lands) need STORAGE_* + ZENYA_* to start.
public static class ZenyaSyncServiceCollectionExtensions
{
    public static IServiceCollection AddZenyaSync(this IServiceCollection services, IConfiguration configuration)
    {
        var options = ZenyaSyncOptions.FromConfiguration(configuration);
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);

        // Same credential AddZenyaClient uses for the assertion: under the ADO service connection
        // it is AzureCliCredential (our identity), on a Function App the managed identity. The
        // storage roles that identity holds are in infra/zenya_sync_identity.tf.
        services.TryAddSingleton<TokenCredential>(_ => new DefaultAzureCredential());
        services.TryAddSingleton<IBlobStore, BlobStore>();
        services.AddSingleton<IZenyaDocumentStore>(sp => new BlobZenyaDocumentStore(
            new BlobServiceClient(options.StorageAccountUrl, sp.GetRequiredService<TokenCredential>())
                .GetBlobContainerClient(options.StorageContainer),
            sp.GetRequiredService<IBlobStore>()));
        services.AddSingleton<ZenyaSyncService>();
        return services;
    }
}
