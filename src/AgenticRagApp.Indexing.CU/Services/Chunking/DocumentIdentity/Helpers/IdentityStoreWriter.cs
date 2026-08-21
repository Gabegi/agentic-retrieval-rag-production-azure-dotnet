using Microsoft.Extensions.Logging;
using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Infrastructure.Clients.DocumentIdentity;

namespace AgenticRagApp.Indexing.CU.Services;

// The only step that writes to the identity store.
//
// Persists this run's documents whose record actually changed, plus any older,
// not-touched-this-run document whose FamilyId shifted because a document processed in this run
// merged it into a bigger cluster. Store-only, not re-uploaded to Search (see
// DocumentIdentityResolver's class comment).
//
// This used to write every document in the run unconditionally, which meant a run where nothing
// changed still paid one blob write per document - 51 round trips to store bytes identical to
// what was already there. The hash already tells us whether the identity moved; the remaining
// fields are compared directly because a family id can change without the identity text changing
// at all (a new document joining the cluster does exactly that).
//
// Writes are sequential: at this corpus's scale that is a handful of round trips. If the corpus
// grows into the thousands this is the first place to batch.
public static class IdentityStoreWriter
{
    public static async Task<PersistOutcome> PersistAsync(
        IDocumentIdentityStore store,
        ILogger logger,
        IReadOnlyList<DocumentIdentity> thisRun,
        IReadOnlyDictionary<string, WorkingDoc> working,
        IReadOnlyDictionary<string, DocumentIdentityRecord> persisted,
        IReadOnlyDictionary<string, string> familyIdOf,
        IReadOnlyDictionary<string, float[]> freshVectors,
        string embeddingModelId,
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
                d.SourceId, d.Title, d.DomainTag, w.Vector, familyId, d.Hash, embeddingModelId);

            if (IsUnchanged(record, persisted.GetValueOrDefault(d.SourceId), freshVectors.ContainsKey(d.SourceId)))
            {
                unchanged++;
                continue;
            }

            await store.SetAsync(record, ct);
            written++;
        }

        // Older documents only ever reach this loop via familyIdOf, which is keyed on the working
        // set, so their EmbeddingModelId already matches the current one and the with-expression
        // carries it through unchanged.
        var thisRunIds = thisRun.Select(d => d.SourceId).ToHashSet();
        foreach (var (sourceId, rec) in persisted)
        {
            if (thisRunIds.Contains(sourceId)) continue;
            if (familyIdOf.TryGetValue(sourceId, out var newFamilyId) && newFamilyId != rec.FamilyId)
            {
                logger.LogInformation(
                    "DocumentIdentityResolver: {SourceId} moved from family {Old} to {New} (store only, Search chunks unchanged)",
                    sourceId, rec.FamilyId, newFamilyId);

                await store.SetAsync(rec with { FamilyId = newFamilyId }, ct);
                moves.Add(new FamilyMove(sourceId, rec.FamilyId, newFamilyId));
                written++;
            }
        }

        return new PersistOutcome(moves, written, unchanged);
    }

    // A record is unchanged when every stored field already matches what this run would write.
    // The identity hash covers the identity text and the model id, but NOT the family id or the
    // resolved title/tag, so those are compared on their own - a document's family can change
    // while its own identity text does not, which is precisely what happens when another document
    // joins its cluster.
    //
    // A freshly embedded vector always counts as changed: it is a different array, and the point
    // of embedding was to store it.
    private static bool IsUnchanged(DocumentIdentityRecord candidate, DocumentIdentityRecord? stored, bool freshlyEmbedded) =>
        stored is not null
        && !freshlyEmbedded
        && stored.IdentityTextHash == candidate.IdentityTextHash
        && stored.FamilyId         == candidate.FamilyId
        && stored.Title            == candidate.Title
        && stored.DomainTag        == candidate.DomainTag
        && stored.EmbeddingModelId == candidate.EmbeddingModelId
        && stored.Vector.Length    == candidate.Vector.Length;
}

// What PersistAsync did, for the run report: which older documents were re-homed, and how many
// records were actually written versus already current.
public sealed record PersistOutcome(
    IReadOnlyList<FamilyMove> Moves, int RecordsWritten, int RecordsUnchanged);
