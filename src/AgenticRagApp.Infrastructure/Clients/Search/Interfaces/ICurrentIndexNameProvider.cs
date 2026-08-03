namespace AgenticRagApp.Infrastructure.Clients.Search;

// Tracks which physical index name is "live" right now, so a restore can build the next
// generation (e.g. zenya-pdf-index-20260730153000) alongside the current one instead of
// deleting it in place - see IIndexService.CreateNextGenerationIndexAsync. Every reader/writer
// of the shared index (SearchDocumentStore, ChunkNeighborExpander, KnowledgeService) resolves
// the index name through here rather than reading IndexerConfig.SearchIndexName directly, so a
// generation promotion takes effect without redeploying.
public interface ICurrentIndexNameProvider
{
    // Falls back to IndexerConfig.SearchIndexName when no pointer blob exists yet (first
    // deploy, or any environment that predates generations) - that value is generation zero.
    Task<string> GetCurrentIndexNameAsync(CancellationToken ct = default);

    // Promotes indexName to "current". Callers are expected to have already fully populated
    // it (IRestoreService) before promoting - this alone doesn't touch index content.
    Task SetCurrentIndexNameAsync(string indexName, CancellationToken ct = default);
}
