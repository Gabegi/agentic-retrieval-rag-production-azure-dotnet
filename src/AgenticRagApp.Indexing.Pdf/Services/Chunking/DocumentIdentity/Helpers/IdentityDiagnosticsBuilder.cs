using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Assembles the run report's identity section out of what the other steps returned. Pure
// composition - every value here was decided by a step that ran earlier; nothing is recomputed.
//
// The thresholds are stamped in alongside the results on purpose: a report that says "3 families"
// without saying at which similarity cannot be compared against a report from a run with a
// different threshold.
public static class IdentityDiagnosticsBuilder
{
    public static IdentityResolutionDiagnostics Build(
        string embeddingModelId,
        int documentsIn,
        IReadOnlyList<DocumentIdentity> thisRun,
        ComparisonSet comparisonSet,
        int persistedRecordsLoaded,
        IReadOnlyDictionary<string, float[]> freshVectors,
        IReadOnlyList<string> skippedEmptyIdentity,
        IReadOnlyList<IdentityTokenPressure> nearingTokenLimit,
        IReadOnlyList<FamilyDiagnostic> families,
        FamilyAssignment assignment,
        IReadOnlyList<SimilarityPair> nearMisses,
        ConfusableResult confusable,
        PersistOutcome persistOutcome)
    {
        var working = comparisonSet.Docs;

        return new IdentityResolutionDiagnostics(
            EmbeddingModelId:                 embeddingModelId,
            DocumentsIn:                      documentsIn,
            ComparisonSetSize:                working.Count,
            PersistedRecordsLoaded:           persistedRecordsLoaded,
            PersistedExcludedNoVector:        comparisonSet.SkippedNoVector,
            PersistedExcludedOtherModel:      comparisonSet.SkippedOtherModel,
            PersistedExcludedWrongDimensions: comparisonSet.SkippedWrongDimensions,
            VectorsEmbedded:                  freshVectors.Count,
            VectorsReused:                    thisRun.Count(d => working.ContainsKey(d.SourceId)
                                                                 && !freshVectors.ContainsKey(d.SourceId)),
            SkippedEmptyIdentity:             skippedEmptyIdentity,
            MaxIdentityTokens:                thisRun.Max(d => d.IdentityTokens),
            TotalIdentityTokensEmbedded:      thisRun.Where(d => freshVectors.ContainsKey(d.SourceId))
                                                     .Sum(d => d.IdentityTokens),
            NearingTokenLimit:                nearingTokenLimit,
            IdentityTokenLimit:               DocumentIdentityBuilder.InputTokenLimit,
            Families:                         families,
            FamilyAssignments:                assignment.Decisions,
            NearMisses:                       nearMisses,
            ConfusableMatches:                confusable.Matches,
            FamilyMovesInStore:               persistOutcome.Moves,
            RecordsWritten:                   persistOutcome.RecordsWritten,
            RecordsUnchanged:                 persistOutcome.RecordsUnchanged,
            SimilarityThreshold:              CosineSimilarityClusterer.SimilarityThreshold,
            NearMissFloor:                    CosineSimilarityClusterer.NearMissFloor,
            ConfusableWordThreshold:          ConfusableTitleDetector.ConfusableWordThreshold,
            MaxConfusableEdits:               ConfusableTitleDetector.MaxConfusableEdits,
            MinConfusableWordLength:          ConfusableTitleDetector.MinConfusableWordLength);
    }

    // No identities, or none that survived to the comparison set: still returns diagnostics so
    // the run report can say what came in and what was skipped, rather than showing an empty
    // section that reads like "identity resolution never ran".
    public static IdentityResolutionResult Empty(
        string embeddingModelId, int documentsIn, IReadOnlyList<string> skippedEmptyIdentity) =>
        new(new Dictionary<string, DocumentFamily>(),
            new IdentityResolutionDiagnostics(
                EmbeddingModelId:                 embeddingModelId,
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
}
