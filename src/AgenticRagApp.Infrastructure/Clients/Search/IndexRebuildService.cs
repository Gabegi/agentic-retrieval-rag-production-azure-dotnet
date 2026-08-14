using Microsoft.Extensions.Logging;

namespace AgenticRagApp.Infrastructure.Clients.Search;

// Composes IIndexService and IKnowledgeService - both peers over the same SearchIndexClient,
// neither of which can own the cross-cutting teardown/rebuild order on its own. See
// IIndexRebuildService for why that order is what it is.
public class IndexRebuildService : IIndexRebuildService
{
    private readonly IIndexService     _indexService;
    private readonly IKnowledgeService _knowledgeService;
    private readonly ILogger<IndexRebuildService> _logger;

    public IndexRebuildService(
        IIndexService     indexService,
        IKnowledgeService knowledgeService,
        ILogger<IndexRebuildService> logger)
    {
        _indexService     = indexService;
        _knowledgeService = knowledgeService;
        _logger           = logger;
    }

    public async Task RecreateEmptyAsync(CancellationToken ct = default)
    {
        _logger.LogWarning(
            "Index rebuild starting — knowledge base and source will be torn down, index dropped and recreated empty");

        await _knowledgeService.DeleteKnowledgeBaseAsync(ct);
        await _knowledgeService.DeleteKnowledgeSourceAsync(ct);
        await _indexService.RecreateIndexAsync();
        await _knowledgeService.EnsureKnowledgeSourceAsync(ct);
        await _knowledgeService.EnsureKnowledgeBaseAsync(ct);

        _logger.LogInformation(
            "Index rebuild complete — index is empty until a restore or reindex repopulates it");
    }
}
