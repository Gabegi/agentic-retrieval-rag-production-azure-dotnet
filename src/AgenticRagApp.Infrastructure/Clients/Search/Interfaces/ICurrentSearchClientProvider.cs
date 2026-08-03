using Azure.Search.Documents;

namespace AgenticRagApp.Infrastructure.Clients.Search;

// Every direct SearchClient consumer (SearchDocumentStore, ChunkNeighborExpander) resolves its
// client through here instead of taking a SearchClient bound once at startup, so a generation
// promotion (ICurrentIndexNameProvider) takes effect on the next call rather than needing a
// redeploy - see ICurrentIndexNameProvider's own comment for why the index name isn't static.
public interface ICurrentSearchClientProvider
{
    Task<SearchClient> GetClientAsync(CancellationToken ct = default);
}
