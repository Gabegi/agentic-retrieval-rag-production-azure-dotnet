using Azure.Search.Documents.Indexes.Models;

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

    // The schema this service would create, without touching the service. Exposed because
    // get-or-create means "what the code declares" and "what is actually live" can disagree
    // indefinitely: a field added or a flag flipped in a deployed build reaches an existing
    // index only through RecreateIndexAsync. Anything that needs to detect that gap compares
    // this against the live definition - see IndexSchemaComparer, and the drift check in the
    // eval suite's ClassInit, which is what stops a run scoring the app against the old shape.
    SearchIndex BuildDefinition();
}
