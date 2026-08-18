using Microsoft.Extensions.Logging;
using AgenticRagApp.Infrastructure.Clients.DocumentIdentity;
using AgenticRagApp.Infrastructure.Clients.Embedding;
using AgenticRagApp.Infrastructure.Configuration;
using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Indexing.Pdf.Utils;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Pre-chunking action items C2 (family/near-duplicate detection) and C3 (title-distance
// check) — docs/2608/260811/pre-chunking-action-items.md and chunking-signals-map.md §3c.
//
// Called once per ChunkActivity run, before PdfExtractionDocuments are handed to the
// splitter (ChunkingService.ChunkDocumentsAsync). Resolves a FamilyId/DomainTag/
// ConfusableWith set per SourceId, which the caller then stamps onto every chunk of that
// document: the same "resolve once at document level, carry onto every chunk" pattern
// DocumentProfile already uses.
//
// This class orchestrates; the three steps that are pure functions live next door in Utils
// and are tested directly (docs/2608/260814/documentidentityresolver-fixes.md, N5):
//   DocumentIdentityBuilder    - what gets embedded, and the hash that decides re-embedding
//   CosineSimilarityClusterer  - FamilyId, plus the diagnostics the calibration pass needs
//   ConfusableTitleDetector    - ConfusableWith, plus which words collided
// Only this class holds the embedding client, the identity store and the logger, so all
// logging happens here on values the components return.
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
// diagnostics logged below exist specifically to make that calibration pass easy.
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

    // How many near-miss pairs to log. All of them are kept in the returned diagnostics; this
    // only bounds the log line, which is read live rather than analysed.
    private const int NearMissesToLog = 5;

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

        foreach (var sourceId in built.SkippedEmptyIdentity)
            _logger.LogWarning(
                "DocumentIdentityResolver: {SourceId} has no title and no headings, so there is nothing to embed - skipped",
                sourceId);

        // The identity text has no cap: every heading goes in. Measured over the real corpus
        // nothing is close to the limit (worst case 73%), so capping would force a full
        // re-embed for no benefit - but the failure past the limit is a SILENT truncation, so
        // the margin is watched rather than assumed. See DocumentIdentityBuilder's constants.
        var nearingTokenLimit = thisRun
            .Where(d => d.IdentityTokens > DocumentIdentityBuilder.TokenWarningThreshold)
            .OrderByDescending(d => d.IdentityTokens)
            .Select(d => new IdentityTokenPressure(d.SourceId, d.IdentityTokens))
            .ToList();

        foreach (var d in nearingTokenLimit)
            _logger.LogWarning(
                "DocumentIdentityResolver: {SourceId}'s identity text is {Tokens} tokens, over {Percent:P0} of the " +
                "{Limit}-token per-input limit - past the limit the tail of its heading list is silently dropped " +
                "from clustering",
                d.SourceId, d.Tokens, DocumentIdentityBuilder.TokenWarningFraction,
                DocumentIdentityBuilder.InputTokenLimit);

        if (thisRun.Count == 0)
            return Empty(docs.Count, built.SkippedEmptyIdentity);

        var persisted = (await _store.GetAllAsync(ct)).ToDictionary(r => r.SourceId);

        var freshVectors = await EmbedChangedAsync(thisRun, persisted, ct);

        // Working set = every persisted document with a usable, same-model vector, plus this
        // run's documents with their vector/title refreshed to current values. This is the
        // full comparison set that both clustering and the confusable check run over.
        //
        // Persisted records are dropped when they have no vector (nothing to compare), were
        // embedded under a different model (comparable only by accident - cosine across two
        // embedding spaces is not a similarity), or carry a vector of the wrong length for the
        // current configuration. Documents in this run are unaffected: the model id is part of
        // the identity hash, so a model change puts them in toEmbed and they come back with a
        // current-model vector.
        var working              = new Dictionary<string, WorkingDoc>();
        var skippedNoVector      = 0;
        var skippedOtherModel    = 0;
        var skippedWrongDimension = 0;

        foreach (var (sourceId, rec) in persisted)
        {
            if (rec.Vector is not { Length: > 0 })
            {
                skippedNoVector++;
                continue;
            }

            if (rec.EmbeddingModelId != _embeddingModelId)
            {
                skippedOtherModel++;
                continue;
            }

            if (rec.Vector.Length != _embeddingDimensions)
            {
                skippedWrongDimension++;
                continue;
            }

            working[sourceId] = new WorkingDoc(rec.Title, rec.Vector);
        }

        if (skippedNoVector > 0)
            _logger.LogWarning(
                "DocumentIdentityResolver: {Count} persisted identity records have no usable vector and were excluded from clustering",
                skippedNoVector);

        if (skippedOtherModel > 0)
            _logger.LogWarning(
                "DocumentIdentityResolver: {Count} persisted identity records were embedded under a different model than {ModelId} " +
                "and were excluded from clustering until their documents are reindexed",
                skippedOtherModel, _embeddingModelId);

        if (skippedWrongDimension > 0)
            _logger.LogWarning(
                "DocumentIdentityResolver: {Count} persisted identity records carry a vector that is not {Dimensions} long " +
                "and were excluded from clustering",
                skippedWrongDimension, _embeddingDimensions);

        foreach (var d in thisRun)
        {
            // The persisted fallback is only reached when the identity hash matched, which
            // implies the same model (the model id is hashed in), so its vector is safe to
            // reuse. The checks are repeated anyway to keep that invariant local.
            var vector = freshVectors.TryGetValue(d.SourceId, out var fresh)
                ? fresh
                : persisted.TryGetValue(d.SourceId, out var rec)
                  && rec.Vector is { Length: > 0 }
                  && rec.EmbeddingModelId == _embeddingModelId
                  && rec.Vector.Length == _embeddingDimensions
                    ? rec.Vector
                    : null;

            if (vector is null)
            {
                // Defensive: EmbedChangedAsync throws rather than returning a document with
                // no vector, so this is unreachable today. Kept so that a future change there
                // degrades to skipping one document instead of putting a null into the cosine
                // loop. Remove() covers the case where a stale persisted entry was admitted
                // above under a rule that later diverges from the one used here.
                _logger.LogWarning("DocumentIdentityResolver: no vector available for {SourceId}, skipping", d.SourceId);
                working.Remove(d.SourceId);
                continue;
            }

            working[d.SourceId] = new WorkingDoc(d.Title, vector);
        }

        if (working.Count == 0)
        {
            _logger.LogWarning("DocumentIdentityResolver: no documents with usable vectors, returning no families");
            return Empty(docs.Count, built.SkippedEmptyIdentity);
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

        // Cluster diagnostics are keyed on the clusterer's own key, which is not the name the
        // family ends up with - restate them under the assigned id so the report and the
        // chunks agree.
        var families = clusters.Families
            .Select(f => f with { FamilyId = familyIdOf[f.Members[0]] })
            .OrderBy(f => f.FamilyId, StringComparer.Ordinal)
            .ToList();

        LogDiagnostics(thisRun.Count, working.Count, families, clusters.NearMisses, confusable, assignment);

        var persistOutcome = await PersistAsync(thisRun, working, persisted, familyIdOf, freshVectors, ct);

        var resolvedFamilies = thisRun
            .Where(d => working.ContainsKey(d.SourceId))
            .ToDictionary(
                d => d.SourceId,
                d => new DocumentFamily(
                    familyIdOf[d.SourceId],
                    d.DomainTag,
                    confusable.ConfusableOf.TryGetValue(d.SourceId, out var c) ? c : []));

        var diagnostics = new IdentityResolutionDiagnostics(
            EmbeddingModelId:                 _embeddingModelId,
            DocumentsIn:                      docs.Count,
            ComparisonSetSize:                working.Count,
            PersistedRecordsLoaded:           persisted.Count,
            PersistedExcludedNoVector:        skippedNoVector,
            PersistedExcludedOtherModel:      skippedOtherModel,
            PersistedExcludedWrongDimensions: skippedWrongDimension,
            VectorsEmbedded:                  freshVectors.Count,
            VectorsReused:                    thisRun.Count(d => working.ContainsKey(d.SourceId)
                                                                 && !freshVectors.ContainsKey(d.SourceId)),
            SkippedEmptyIdentity:             built.SkippedEmptyIdentity,
            MaxIdentityTokens:                thisRun.Max(d => d.IdentityTokens),
            TotalIdentityTokensEmbedded:      thisRun.Where(d => freshVectors.ContainsKey(d.SourceId))
                                                     .Sum(d => d.IdentityTokens),
            NearingTokenLimit:                nearingTokenLimit,
            IdentityTokenLimit:               DocumentIdentityBuilder.InputTokenLimit,
            Families:                         families,
            FamilyAssignments:                assignment.Decisions,
            NearMisses:                       clusters.NearMisses,
            ConfusableMatches:                confusable.Matches,
            FamilyMovesInStore:               persistOutcome.Moves,
            RecordsWritten:                   persistOutcome.RecordsWritten,
            RecordsUnchanged:                 persistOutcome.RecordsUnchanged,
            SimilarityThreshold:              CosineSimilarityClusterer.SimilarityThreshold,
            NearMissFloor:                    CosineSimilarityClusterer.NearMissFloor,
            ConfusableWordThreshold:          ConfusableTitleDetector.ConfusableWordThreshold,
            MaxConfusableEdits:               ConfusableTitleDetector.MaxConfusableEdits,
            MinConfusableWordLength:          ConfusableTitleDetector.MinConfusableWordLength);

        // Which documents got a vector this run vs reused one is per-document detail the
        // caller stamps onto its report rows, so it travels as a lookup rather than a count.
        var vectorSource = thisRun
            .Where(d => working.ContainsKey(d.SourceId))
            .ToDictionary(
                d => d.SourceId,
                d => freshVectors.ContainsKey(d.SourceId) ? "embedded" : "reused");

        // The two halves of "whose family_id changed": documents in this run's comparison set,
        // and previously-indexed documents this run's clustering re-homed. A document cannot be
        // in both - PersistAsync skips this run's ids - so concatenating is the union.
        var familyMoves = assignment.InRunMoves
            .Concat(persistOutcome.Moves)
            .OrderBy(m => m.SourceId, StringComparer.Ordinal)
            .ToList();

        return new IdentityResolutionResult(resolvedFamilies, diagnostics, vectorSource, familyMoves);
    }

    // No identities, or none that survived to the comparison set: still returns diagnostics so
    // the run report can say what came in and what was skipped, rather than showing an empty
    // section that reads like "identity resolution never ran".
    private IdentityResolutionResult Empty(int documentsIn, IReadOnlyList<string> skippedEmptyIdentity) =>
        new(new Dictionary<string, DocumentFamily>(),
            new IdentityResolutionDiagnostics(
                EmbeddingModelId:                 _embeddingModelId,
                DocumentsIn:                      documentsIn,
                ComparisonSetSize:                0,
                PersistedRecordsLoaded:           0,
                PersistedExcludedNoVector:        0,
                PersistedExcludedOtherModel:      0,
                PersistedExcludedWrongDimensions: 0,
                VectorsEmbedded:                  0,
                VectorsReused:                    0,
                SkippedEmptyIdentity:             skippedEmptyIdentity,
                MaxIdentityTokens:                0,
                TotalIdentityTokensEmbedded:      0,
                NearingTokenLimit:                [],
                IdentityTokenLimit:               DocumentIdentityBuilder.InputTokenLimit,
                Families:                         [],
                FamilyAssignments:                [],
                NearMisses:                       [],
                ConfusableMatches:                [],
                FamilyMovesInStore:               [],
                RecordsWritten:                   0,
                RecordsUnchanged:                 0,
                SimilarityThreshold:              CosineSimilarityClusterer.SimilarityThreshold,
                NearMissFloor:                    CosineSimilarityClusterer.NearMissFloor,
                ConfusableWordThreshold:          ConfusableTitleDetector.ConfusableWordThreshold,
                MaxConfusableEdits:               ConfusableTitleDetector.MaxConfusableEdits,
                MinConfusableWordLength:          ConfusableTitleDetector.MinConfusableWordLength),
            new Dictionary<string, string>(),
            []);

    // Everything the calibration pass needs, on values the pure components returned. Both
    // directions of the threshold question are covered: the weakest intra-family link shows
    // over-merging, the near misses show what the threshold kept apart. Confusable matches
    // carry the words that collided, so a flag can be judged without re-deriving it by hand.
    private void LogDiagnostics(
        int identitiesInRun, int comparisonSetSize,
        IReadOnlyList<FamilyDiagnostic> families,
        IReadOnlyList<SimilarityPair> nearMisses,
        ConfusableResult confusable,
        FamilyAssignment assignment)
    {
        foreach (var family in families)
            _logger.LogInformation(
                "DocumentIdentityResolver: family {FamilyId} has {Size} members, weakest intra-family similarity {Weakest:F3}",
                family.FamilyId, family.Members.Count, family.WeakestSimilarity);

        // Anything other than Kept/Minted means a family's composition changed - the case that
        // used to rename families silently, and the one worth seeing in a live run.
        foreach (var decision in assignment.Decisions.Where(d => d.Kind is not FamilyAssignmentKind.Kept))
            _logger.LogInformation(
                "DocumentIdentityResolver: family {FamilyId} {Kind} ({Members} member(s)){Detail}",
                decision.FamilyId, decision.Kind.ToString().ToLowerInvariant(), decision.Members.Count,
                decision.Detail is null ? "" : $" - {decision.Detail}");

        foreach (var pair in nearMisses.Take(NearMissesToLog))
            _logger.LogInformation(
                "DocumentIdentityResolver: near miss - {SourceIdA} ~ {SourceIdB} at {Similarity:F3}, below the {Threshold:F2} threshold",
                pair.SourceIdA, pair.SourceIdB, pair.Similarity, CosineSimilarityClusterer.SimilarityThreshold);

        foreach (var match in confusable.Matches)
            _logger.LogInformation(
                "DocumentIdentityResolver: confusable - {SourceId} vs {OtherSourceId} on '{Word}'/'{OtherWord}'",
                match.SourceId, match.OtherSourceId, match.Word, match.OtherWord);

        _logger.LogInformation(
            "DocumentIdentityResolver: resolved {Identities} document(s) against a comparison set of {ComparisonSet}, " +
            "producing {Families} multi-member famil{FamilySuffix}, {NearMisses} near-miss pair(s) and {Confusable} confusable relation(s)",
            identitiesInRun, comparisonSetSize, families.Count,
            families.Count == 1 ? "y" : "ies",
            nearMisses.Count, confusable.Matches.Count);
    }

    private async Task<Dictionary<string, float[]>> EmbedChangedAsync(
        IReadOnlyList<DocumentIdentity> thisRun,
        Dictionary<string, DocumentIdentityRecord> persisted,
        CancellationToken ct)
    {
        var toEmbed = thisRun
            .Where(d => !persisted.TryGetValue(d.SourceId, out var rec)
                        || rec.IdentityTextHash != d.Hash
                        || rec.Vector is not { Length: > 0 })
            .ToList();

        var vectors = new Dictionary<string, float[]>();
        if (toEmbed.Count == 0)
            return vectors;

        // One call for the whole run: identity texts are a title plus a heading list, so even
        // the full 51-document corpus is a single modest batch. If the corpus grows past what
        // one request accepts, batch here the way CsvEmbeddingService does.
        var (embedded, retries) = await _embeddingClient.EmbedWithRetryAsync(
            toEmbed.Select(d => d.IdentityText).ToList(), ct);

        if (retries > 0)
            _logger.LogInformation(
                "DocumentIdentityResolver: {Retries} embedding retries for {Count} identity vectors", retries, toEmbed.Count);

        // Results are matched to inputs positionally, so a short or long result set means the
        // pairing is unreliable. Failing here is much cheaper than persisting document A's
        // vector under document B's SourceId and having the wrong families silently stick.
        // This throws out of ChunkDocumentsAsync and fails the whole chunking activity, which
        // is deliberate: a partial identity pass writes durable, wrong records to the store.
        if (embedded is null || embedded.Length != toEmbed.Count)
            throw new InvalidOperationException(
                $"DocumentIdentityResolver: embedding client returned {embedded?.Length ?? 0} vectors for {toEmbed.Count} inputs; " +
                "cannot map vectors to documents.");

        for (int i = 0; i < toEmbed.Count; i++)
        {
            if (embedded[i] is not { Length: > 0 })
                throw new InvalidOperationException(
                    $"DocumentIdentityResolver: embedding client returned an empty vector for {toEmbed[i].SourceId}.");

            // Same reasoning as the count check above, for the same reason it throws rather
            // than logging: the sibling embedders can log and drop a bad chunk because the next
            // run re-embeds it, but a wrong-dimension vector persisted here stays in the
            // comparison set and quietly turns its document into a family of one.
            if (embedded[i].Length != _embeddingDimensions)
                throw new InvalidOperationException(
                    $"DocumentIdentityResolver: embedding client returned a {embedded[i].Length}-dimension vector for " +
                    $"{toEmbed[i].SourceId}, expected {_embeddingDimensions}.");

            vectors[toEmbed[i].SourceId] = embedded[i];
        }

        return vectors;
    }

    // Persists this run's documents whose record actually changed, plus any older,
    // not-touched-this-run document whose FamilyId shifted because a document processed in this
    // run merged it into a bigger cluster. Store-only, not re-uploaded to Search (see class
    // comment).
    //
    // This used to write every document in the run unconditionally, which meant a run where
    // nothing changed still paid one blob write per document - 51 round trips to store bytes
    // identical to what was already there. The hash already tells us whether the identity
    // moved; the remaining fields are compared directly because a family id can change without
    // the identity text changing at all (a new document joining the cluster does exactly that).
    //
    // Writes are sequential: at this corpus's scale that is a handful of round trips. If the
    // corpus grows into the thousands this is the first place to batch.
    private async Task<PersistOutcome> PersistAsync(
        IReadOnlyList<DocumentIdentity> thisRun,
        Dictionary<string, WorkingDoc> working,
        Dictionary<string, DocumentIdentityRecord> persisted,
        IReadOnlyDictionary<string, string> familyIdOf,
        IReadOnlyDictionary<string, float[]> freshVectors,
        CancellationToken ct)
    {
        var moves     = new List<FamilyMove>();
        var written   = 0;
        var unchanged = 0;

        foreach (var d in thisRun)
        {
            if (!working.TryGetValue(d.SourceId, out var w)) continue;
            if (!familyIdOf.TryGetValue(d.SourceId, out var familyId)) continue;

            var record = new DocumentIdentityRecord(
                d.SourceId, d.Title, d.DomainTag, w.Vector, familyId, d.Hash, _embeddingModelId);

            if (IsUnchanged(record, persisted.GetValueOrDefault(d.SourceId), freshVectors.ContainsKey(d.SourceId)))
            {
                unchanged++;
                continue;
            }

            await _store.SetAsync(record, ct);
            written++;
        }

        // Older documents only ever reach this loop via familyIdOf, which is keyed on the
        // working set, so their EmbeddingModelId already matches the current one and the
        // `with` expression carries it through unchanged.
        var thisRunIds = thisRun.Select(d => d.SourceId).ToHashSet();
        foreach (var (sourceId, rec) in persisted)
        {
            if (thisRunIds.Contains(sourceId)) continue;
            if (familyIdOf.TryGetValue(sourceId, out var newFamilyId) && newFamilyId != rec.FamilyId)
            {
                _logger.LogInformation(
                    "DocumentIdentityResolver: {SourceId} moved from family {Old} to {New} (store only, Search chunks unchanged)",
                    sourceId, rec.FamilyId, newFamilyId);

                await _store.SetAsync(rec with { FamilyId = newFamilyId }, ct);
                moves.Add(new FamilyMove(sourceId, rec.FamilyId, newFamilyId));
                written++;
            }
        }

        return new PersistOutcome(moves, written, unchanged);
    }

    // A record is unchanged when every stored field already matches what this run would write.
    // The identity hash covers the identity text and the model id, but NOT the family id or
    // the resolved title/tag, so those are compared on their own - a document's family can
    // change while its own identity text does not, which is precisely what happens when
    // another document joins its cluster.
    //
    // A freshly embedded vector always counts as changed: it is a different array, and the
    // point of embedding was to store it.
    private bool IsUnchanged(DocumentIdentityRecord candidate, DocumentIdentityRecord? stored, bool freshlyEmbedded) =>
        stored is not null
        && !freshlyEmbedded
        && stored.IdentityTextHash == candidate.IdentityTextHash
        && stored.FamilyId         == candidate.FamilyId
        && stored.Title            == candidate.Title
        && stored.DomainTag        == candidate.DomainTag
        && stored.EmbeddingModelId == candidate.EmbeddingModelId
        && stored.Vector.Length    == candidate.Vector.Length;

    private readonly record struct WorkingDoc(string Title, float[] Vector);

    // What PersistAsync did, for the run report: which older documents were re-homed, and how
    // many records were actually written versus already current.
    private sealed record PersistOutcome(
        IReadOnlyList<FamilyMove> Moves, int RecordsWritten, int RecordsUnchanged);
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
