using Microsoft.Extensions.Logging;
using AgenticRagApp.Infrastructure.Clients.DocumentIdentity;

namespace AgenticRagApp.Indexing.Pdf.Services;

// The set of documents clustering and the confusable check actually run over: every persisted
// document with a usable, same-model vector, plus this run's documents with their vector/title
// refreshed to current values.
//
// Split out of DocumentIdentityResolver because it is the one step with real admission RULES,
// and those rules are what a wrong family id usually traces back to. All three exclusions are
// counted rather than silently applied - the counts land in the run report.
public static class IdentityComparisonSet
{
    // Persisted records are dropped when they have no vector (nothing to compare), were embedded
    // under a different model (comparable only by accident - cosine across two embedding spaces
    // is not a similarity), or carry a vector of the wrong length for the current configuration.
    // Documents in this run are unaffected: the model id is part of the identity hash, so a model
    // change puts them in toEmbed and they come back with a current-model vector.
    public static ComparisonSet Build(
        IReadOnlyList<DocumentIdentity> thisRun,
        IReadOnlyDictionary<string, DocumentIdentityRecord> persisted,
        IReadOnlyDictionary<string, float[]> freshVectors,
        string embeddingModelId,
        int embeddingDimensions,
        ILogger logger)
    {
        var working               = new Dictionary<string, WorkingDoc>();
        var skippedNoVector       = 0;
        var skippedOtherModel     = 0;
        var skippedWrongDimension = 0;

        foreach (var (sourceId, rec) in persisted)
        {
            if (rec.Vector is not { Length: > 0 })
            {
                skippedNoVector++;
                continue;
            }

            if (rec.EmbeddingModelId != embeddingModelId)
            {
                skippedOtherModel++;
                continue;
            }

            if (rec.Vector.Length != embeddingDimensions)
            {
                skippedWrongDimension++;
                continue;
            }

            working[sourceId] = new WorkingDoc(rec.Title, rec.Vector);
        }

        if (skippedNoVector > 0)
            logger.LogWarning(
                "DocumentIdentityResolver: {Count} persisted identity records have no usable vector and were excluded from clustering",
                skippedNoVector);

        if (skippedOtherModel > 0)
            logger.LogWarning(
                "DocumentIdentityResolver: {Count} persisted identity records were embedded under a different model than {ModelId} " +
                "and were excluded from clustering until their documents are reindexed",
                skippedOtherModel, embeddingModelId);

        if (skippedWrongDimension > 0)
            logger.LogWarning(
                "DocumentIdentityResolver: {Count} persisted identity records carry a vector that is not {Dimensions} long " +
                "and were excluded from clustering",
                skippedWrongDimension, embeddingDimensions);

        foreach (var d in thisRun)
        {
            // The persisted fallback is only reached when the identity hash matched, which
            // implies the same model (the model id is hashed in), so its vector is safe to
            // reuse. The checks are repeated anyway to keep that invariant local.
            var vector = freshVectors.TryGetValue(d.SourceId, out var fresh)
                ? fresh
                : persisted.TryGetValue(d.SourceId, out var rec)
                  && rec.Vector is { Length: > 0 }
                  && rec.EmbeddingModelId == embeddingModelId
                  && rec.Vector.Length == embeddingDimensions
                    ? rec.Vector
                    : null;

            if (vector is null)
            {
                // Defensive: IdentityEmbedder throws rather than returning a document with no
                // vector, so this is unreachable today. Kept so that a future change there
                // degrades to skipping one document instead of putting a null into the cosine
                // loop. Remove() covers the case where a stale persisted entry was admitted
                // above under a rule that later diverges from the one used here.
                logger.LogWarning("DocumentIdentityResolver: no vector available for {SourceId}, skipping", d.SourceId);
                working.Remove(d.SourceId);
                continue;
            }

            working[d.SourceId] = new WorkingDoc(d.Title, vector);
        }

        return new ComparisonSet(working, skippedNoVector, skippedOtherModel, skippedWrongDimension);
    }
}

// One document as the clusterer and the confusable check see it: the title they compare, and the
// vector they compare it by. Deliberately not DocumentIdentityRecord - the record is what the
// store holds, this is what a comparison needs.
public readonly record struct WorkingDoc(string Title, float[] Vector);

// Docs is the comparison set itself; the three counts are the exclusions that produced it and
// travel on to the run report.
public sealed record ComparisonSet(
    Dictionary<string, WorkingDoc> Docs,
    int SkippedNoVector,
    int SkippedOtherModel,
    int SkippedWrongDimensions);
