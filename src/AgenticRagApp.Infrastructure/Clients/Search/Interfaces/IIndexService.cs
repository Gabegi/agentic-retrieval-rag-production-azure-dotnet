namespace AgenticRagApp.Infrastructure.Clients.Search;

// Manages the single shared Azure AI Search index's lifecycle. PDF and CSV chunks both
// land in this one index (queried by one shared QueryingFunction) — there is exactly one
// schema for both, not one per doc-type. EnsureIndexAsync only creates a *missing* index,
// never updates one, specifically to avoid a code-driven push silently overwriting any
// portal-side customisation nobody told this class about.
public interface IIndexService
{
    Task EnsureIndexAsync();

    // Deletes the index (all documents, gone) and recreates it from scratch with the current
    // schema - the "index is corrupt" recovery path, distinct from EnsureIndexAsync's
    // get-or-create. Callers must repopulate afterwards (full reindex or restore-from-snapshot)
    // - this alone leaves the index empty.
    Task RecreateIndexAsync();
}
