using Azure.Search.Documents.Indexes.Models;

namespace AgenticRagApp.Infrastructure.Clients.Search;

// Manages the single Azure AI Search index's lifecycle. Every chunk lands in this one index
// (queried by one QueryingFunction) — there is exactly one schema, not one per doc-type; the
// archived CSV pipeline once shared it. EnsureIndexAsync only creates a *missing* index,
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

    // The vector side of the LIVE definition - metric, width, HNSW parameters, compression,
    // vectorizer - as the service reports it, with the configured width and model stamped
    // beside them (2026-09-15). Read-only. What the run report carries as VectorConfig, and what
    // the run analysis flags dimension / model / metric drift from. See IndexVectorConfig.
    Task<IndexVectorConfig> ReadVectorConfigAsync(CancellationToken ct = default);
}
