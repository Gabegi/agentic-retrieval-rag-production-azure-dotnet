using System.Collections.Concurrent;
using Azure.Search.Documents;

namespace AgenticRagApp.Infrastructure.Clients.Search;

public class CurrentSearchClientProvider : ICurrentSearchClientProvider
{
    private readonly ICurrentIndexNameProvider          _indexNameProvider;
    private readonly Func<string, SearchClient>         _clientFactory;
    private readonly ConcurrentDictionary<string, SearchClient> _clients = new();

    public CurrentSearchClientProvider(ICurrentIndexNameProvider indexNameProvider, Func<string, SearchClient> clientFactory)
    {
        _indexNameProvider = indexNameProvider;
        _clientFactory     = clientFactory;
    }

    public async Task<SearchClient> GetClientAsync(CancellationToken ct = default)
    {
        var indexName = await _indexNameProvider.GetCurrentIndexNameAsync(ct);
        return _clients.GetOrAdd(indexName, _clientFactory);
    }
}
