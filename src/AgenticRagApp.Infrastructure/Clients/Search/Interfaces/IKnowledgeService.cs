namespace AgenticRagApp.Infrastructure.Clients.Search;

public interface IKnowledgeService
{
    Task EnsureKnowledgeSourceAsync(CancellationToken ct = default);
    Task EnsureKnowledgeBaseAsync(CancellationToken ct = default);

    // Teardown counterparts, used by the index restore path (RecreateIndexActivity):
    // Azure AI Search refuses to delete an index while a knowledge source still
    // references it, so the knowledge base and source must be deleted first (base before
    // source - the base references the source, not the other way round) and rebuilt
    // after the index is recreated.
    Task DeleteKnowledgeBaseAsync(CancellationToken ct = default);
    Task DeleteKnowledgeSourceAsync(CancellationToken ct = default);
}
