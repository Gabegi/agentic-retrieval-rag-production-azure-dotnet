namespace AgenticRagApp.Infrastructure.Clients.Search;

// The index and the knowledge stack on top of it cannot be rebuilt independently: Azure AI
// Search refuses to delete an index while a knowledge source still references it. Teardown
// therefore goes base -> source -> index and rebuild goes index -> source -> base. That
// ordering is the whole reason this interface exists - it belongs in one place rather than
// being re-derived at each call site (see docs/2607/260730/index-restore-knowledge-source-plan.md).
//
// Leaves the index EMPTY. Callers repopulate afterwards - restore-from-snapshot
// (RestoreOrchestrator) or a full reindex (StartIndexing?force=true).
public interface IIndexRebuildService
{
    Task RecreateEmptyAsync(CancellationToken ct = default);
}
