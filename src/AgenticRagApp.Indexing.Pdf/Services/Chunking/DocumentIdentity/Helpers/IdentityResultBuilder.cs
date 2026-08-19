using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// The four projections that turn the clustering output into what the caller carries onto chunks
// and onto its report rows. Pure lookups over values the earlier steps returned - kept together
// because each is two lines whose whole content is a comment explaining which key it is on.
public static class IdentityResultBuilder
{
    // Cluster diagnostics are keyed on the clusterer's own key, which is not the name the family
    // ends up with - restate them under the assigned id so the report and the chunks agree.
    public static IReadOnlyList<FamilyDiagnostic> RestateFamilies(
        IReadOnlyList<FamilyDiagnostic> clustered,
        IReadOnlyDictionary<string, string> familyIdOf) =>
        clustered
            .Select(f => f with { FamilyId = familyIdOf[f.Members[0]] })
            .OrderBy(f => f.FamilyId, StringComparer.Ordinal)
            .ToList();

    // The resolved identity a chunk carries: family, domain tag and the documents this one is
    // confusable with. Only this run's documents get one - an older document's chunks are already
    // in Search and nothing here rewrites them.
    public static IReadOnlyDictionary<string, DocumentFamily> ResolvedFamilies(
        IReadOnlyList<DocumentIdentity> thisRun,
        IReadOnlyDictionary<string, WorkingDoc> working,
        IReadOnlyDictionary<string, string> familyIdOf,
        ConfusableResult confusable) =>
        thisRun
            .Where(d => working.ContainsKey(d.SourceId))
            .ToDictionary(
                d => d.SourceId,
                d => new DocumentFamily(
                    familyIdOf[d.SourceId],
                    d.DomainTag,
                    confusable.ConfusableOf.TryGetValue(d.SourceId, out var c) ? c : []));

    // Which documents got a vector this run vs reused one is per-document detail the caller
    // stamps onto its report rows, so it travels as a lookup rather than a count.
    public static IReadOnlyDictionary<string, string> VectorSourceOf(
        IReadOnlyList<DocumentIdentity> thisRun,
        IReadOnlyDictionary<string, WorkingDoc> working,
        IReadOnlyDictionary<string, float[]> freshVectors) =>
        thisRun
            .Where(d => working.ContainsKey(d.SourceId))
            .ToDictionary(
                d => d.SourceId,
                d => freshVectors.ContainsKey(d.SourceId) ? "embedded" : "reused");

    // The two halves of "whose family_id changed": documents in this run's comparison set, and
    // previously-indexed documents this run's clustering re-homed. A document cannot be in both -
    // IdentityStoreWriter skips this run's ids - so concatenating is the union.
    public static IReadOnlyList<FamilyMove> FamilyMoves(
        IReadOnlyList<FamilyMove> inRunMoves,
        IReadOnlyList<FamilyMove> storeMoves) =>
        inRunMoves
            .Concat(storeMoves)
            .OrderBy(m => m.SourceId, StringComparer.Ordinal)
            .ToList();
}
