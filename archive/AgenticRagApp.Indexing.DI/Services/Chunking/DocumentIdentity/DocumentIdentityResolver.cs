using Microsoft.Extensions.Logging;
using AgenticRagApp.Infrastructure.Clients.DocumentIdentity;
using AgenticRagApp.Infrastructure.Clients.Embedding;
using AgenticRagApp.Infrastructure.Configuration;
using AgenticRagApp.Indexing.DI.Models;

namespace AgenticRagApp.Indexing.DI.Services;

// Pre-chunking action items C2 (family/near-duplicate detection) and C3 (title-distance
// check) — docs/2608/260811/pre-chunking-action-items.md and chunking-signals-map.md §3c.
//
// Called once per ChunkActivity run, before PdfExtractionDocuments are handed to the
// splitter (ChunkingService.ChunkDocumentsAsync). Resolves a FamilyId/DomainTag/
// ConfusableWith set per SourceId, which the caller then stamps onto every chunk of that
// document: the same "resolve once at document level, carry onto every chunk" pattern
// DocumentProfile already uses.
//
// An ORCHESTRATOR, the same shape as ChunkMetadataBuilder: every step is one call into
// Helpers, and this class owns the ORDER of the steps and nothing else - no embedding, no
// admission rules, no store writes, and no log line that belongs to a single step. In order:
//   DocumentIdentityBuilder      - what gets embedded, and the hash that decides re-embedding
//   IdentityTokenPressureCheck   - what could not be identified, and what is nearing the limit
//   IdentityEmbedder             - the vectors for the documents whose identity moved
//   IdentityComparisonSet        - which documents clustering is allowed to run over
//   CosineSimilarityClusterer    - the grouping, plus the diagnostics the calibration pass needs
//   FamilyIdAssigner             - the NAME each group ends up with
//   ConfusableTitleDetector      - ConfusableWith, plus which words collided
//   IdentityDiagnosticsLogger    - the live-run log lines
//   IdentityStoreWriter          - what actually gets persisted
//   IdentityResultBuilder        - the per-document lookups the caller stamps onto chunks
//   IdentityDiagnosticsBuilder   - the run report's identity section
// Each helper takes the dependency its step needs (the embedding client, the store, the logger)
// as a parameter rather than holding one, so all of them stay static and directly testable; this
// class holds them only to pass them in.
//
// Not to be confused with DocumentIdentityBuilder, which it calls: the BUILDER composes the
// text a document is identified BY (title + domain tag + headings, and its hash), while this
// RESOLVER turns those identities into the resolved identity a chunk carries (FamilyId,
// DomainTag, ConfusableWith) and persists it.
//
// Family membership is corpus-wide, not run-scoped: a document processed today needs to
// cluster against every document ever indexed, not just whatever else happens to be in this
// run's incremental batch (see IDocumentIdentityStore). Deliberately assign-only, never
// retroactive on Search: if a new document should pull an older, already-indexed document
// into its family, only the identity STORE record for that older document gets its FamilyId
// corrected (so future clustering stays accurate). Its already-uploaded Search chunks keep
// their old family_id until that document happens to be reindexed for some other reason.
// Patching live Search documents on every merge was judged not worth the extra write path
// for a 51-document POC corpus that rarely changes.
//
// The same assign-only asymmetry applies to ConfusableWith, and more sharply: it is only
// computed for documents in the current run. An older document that is confusable with one
// being processed now does not get the flag until it is itself reindexed, because its chunks
// are already in Search and nothing here rewrites them.
//
// Comparisons only ever run between vectors from the same embedding model (see
// EmbeddingModelId on DocumentIdentityRecord). Persisted records from an older model are
// held out of the comparison set entirely rather than cosine-compared across incompatible
// embedding spaces; they rejoin on their next reindex. Changing the model therefore degrades
// clustering to the current run's documents until the corpus has been reprocessed, which is
// the intended trade against silently meaningless similarities.
//
// Thresholds are a starting point, not calibrated against the real corpus yet (no live
// embedding run was done while writing this): same caveat as tokenizer-redo-findings.md had
// before its ratios were measured. Revisit CosineSimilarityClusterer.SimilarityThreshold and
// ConfusableTitleDetector's three constants once this has actually run against the
// 51-document corpus and the resulting clusters/confusable pairs can be checked by hand
// against the known families in chunking-signals-map.md §2. The cluster and confusable
// diagnostics IdentityDiagnosticsLogger writes exist specifically to make that calibration
// pass easy.
public class DocumentIdentityResolver
{
    private readonly IEmbeddingClient          _embeddingClient;
    private readonly IDocumentIdentityStore    _store;
    private readonly ILogger<DocumentIdentityResolver> _logger;

    // Identifies the embedding space the persisted vectors live in. Folded into the identity
    // hash so a model change forces a re-embed of everything this run touches, and stamped
    // onto each record so older-model vectors can be excluded from the comparison set rather
    // than silently mixed into it.
    //
    // Model name rather than deployment name — the deployment is an alias that can be
    // repointed without the embedding space changing — plus the requested dimension count,
    // since the same model at a different dimensionality is a different space too.
    private readonly string _embeddingModelId;

    // Checked against every vector this class accepts. A wrong-dimension vector is otherwise
    // invisible: CosineSimilarity bails to 0 when the lengths differ, so the document compares
    // as maximally dissimilar to everything and becomes its own single-member family - which
    // looks exactly like a corpus with no near-duplicates.
    private readonly int _embeddingDimensions;

    public DocumentIdentityResolver(
        IEmbeddingClient embeddingClient,
        IDocumentIdentityStore store,
        IndexerConfig config,
        ILogger<DocumentIdentityResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(config);

        _embeddingClient     = embeddingClient ?? throw new ArgumentNullException(nameof(embeddingClient));
        _store               = store           ?? throw new ArgumentNullException(nameof(store));
        _logger              = logger          ?? throw new ArgumentNullException(nameof(logger));
        _embeddingModelId    = $"{config.OpenAiEmbeddingModelName}@{config.OpenAiEmbeddingDimensions}";
        _embeddingDimensions = config.OpenAiEmbeddingDimensions;
    }

    public async Task<IdentityResolutionResult> ResolveDocumentIdentityAsync(
        IReadOnlyList<PdfExtractionDocument> docs, CancellationToken ct = default)
    {
        var built   = DocumentIdentityBuilder.Build(docs, _embeddingModelId);
        var thisRun = built.Identities;

        var nearingTokenLimit = IdentityTokenPressureCheck.Run(thisRun, built.SkippedEmptyIdentity, _logger);

        if (thisRun.Count == 0)
            return IdentityDiagnosticsBuilder.Empty(_embeddingModelId, docs.Count, built.SkippedEmptyIdentity);

        var persisted = (await _store.GetAllAsync(ct)).ToDictionary(r => r.SourceId);

        var freshVectors = await IdentityEmbedder.EmbedChangedAsync(
            _embeddingClient, _logger, thisRun, persisted, _embeddingDimensions, ct);

        var comparisonSet = IdentityComparisonSet.Build(
            thisRun, persisted, freshVectors, _embeddingModelId, _embeddingDimensions, _logger);

        var working = comparisonSet.Docs;

        if (working.Count == 0)
        {
            _logger.LogWarning("DocumentIdentityResolver: no documents with usable vectors, returning no families");
            return IdentityDiagnosticsBuilder.Empty(_embeddingModelId, docs.Count, built.SkippedEmptyIdentity);
        }

        var clusters = CosineSimilarityClusterer.Cluster(
            working.ToDictionary(kv => kv.Key, kv => kv.Value.Vector));

        // Naming is a separate decision from grouping: the clusterer's key is recomputed from
        // vectors every run, whereas a family id has to survive its membership changing (see
        // FamilyIdAssigner). Everything downstream uses the assigned id, not the cluster key.
        var assignment = FamilyIdAssigner.Assign(
            clusters.FamilyIdOf,
            working.ToDictionary(kv => kv.Key, kv => kv.Value.Title),
            working.Keys.ToDictionary(
                id => id,
                id => persisted.TryGetValue(id, out var rec) ? rec.FamilyId : null));

        var familyIdOf = assignment.FamilyIdOf;

        var confusable = ConfusableTitleDetector.Detect(
            thisRun.Where(d => working.ContainsKey(d.SourceId)).Select(d => d.SourceId).ToList(),
            working.ToDictionary(kv => kv.Key, kv => kv.Value.Title),
            familyIdOf);

        var families = IdentityResultBuilder.RestateFamilies(clusters.Families, familyIdOf);

        IdentityDiagnosticsLogger.Log(
            _logger, thisRun.Count, working.Count, families, clusters.NearMisses, confusable, assignment);

        var persistOutcome = await IdentityStoreWriter.PersistAsync(
            _store, _logger, thisRun, working, persisted, familyIdOf, freshVectors, _embeddingModelId, ct);

        var diagnostics = IdentityDiagnosticsBuilder.Build(
            _embeddingModelId, docs.Count, thisRun, comparisonSet, persisted.Count, freshVectors,
            built.SkippedEmptyIdentity, nearingTokenLimit, families, assignment, clusters.NearMisses,
            confusable, persistOutcome);

        return new IdentityResolutionResult(
            IdentityResultBuilder.ResolvedFamilies(thisRun, working, familyIdOf, confusable),
            diagnostics,
            IdentityResultBuilder.VectorSourceOf(thisRun, working, freshVectors),
            IdentityResultBuilder.FamilyMoves(assignment.InRunMoves, persistOutcome.Moves));
    }
}

// Families is what the caller stamps onto chunks; the rest is what the run report needs to
// explain the result. Diagnostics is never null, even on the empty paths - a report that says
// "0 documents in, 2 skipped for an empty identity text" is useful, an absent section is not.
//
// IdentityVectorSourceOf is per-document ("embedded" this run, or "reused" from the store),
// keyed by SourceId and only containing documents that made it into the comparison set.
public sealed record IdentityResolutionResult(
    IReadOnlyDictionary<string, DocumentFamily> Families,
    IdentityResolutionDiagnostics               Diagnostics,
    IReadOnlyDictionary<string, string>         IdentityVectorSourceOf,

    // Every document whose family_id changed this run - those in the comparison set
    // (FamilyAssignment.InRunMoves) and those merely re-homed by it (PersistOutcome.Moves).
    //
    // This exists because nothing downstream can detect a family move on its own. family_id is
    // not part of the embedded text, so a chunk's hash - hash(embedString + model id) - is
    // byte-identical before and after; the indexer's diff sees "in both, skip" and the index
    // keeps the old family_id indefinitely. The document's own IdentityHash does not catch it
    // either: a move is caused by OTHER documents changing the clustering, so this document is
    // unchanged and the skip gate skips it before chunking runs.
    //
    // So this set is the signal: the indexer force-writes these documents' rows regardless of
    // what the hash diff says. Deliberately NOT solved by putting family_id in the hash, which
    // would re-embed every chunk to produce byte-identical vectors and change chunk_id - in
    // Search a delete plus insert rather than a field update.
    IReadOnlyList<FamilyMove>                   FamilyMoves);
