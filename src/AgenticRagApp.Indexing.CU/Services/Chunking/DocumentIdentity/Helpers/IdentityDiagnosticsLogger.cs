using Microsoft.Extensions.Logging;

namespace AgenticRagApp.Indexing.CU.Services;

// Everything the calibration pass needs, on values the pure components returned. Both directions
// of the threshold question are covered: the weakest intra-family link shows over-merging, the
// near misses show what the threshold kept apart. Confusable matches carry the words that
// collided, so a flag can be judged without re-deriving it by hand.
//
// Log-only: it computes nothing the run report does not already carry. Split out of
// DocumentIdentityResolver so the orchestrator reads as a list of steps rather than a list of
// steps interleaved with log statements.
public static class IdentityDiagnosticsLogger
{
    // How many near-miss pairs to log. All of them are kept in the returned diagnostics; this
    // only bounds the log line, which is read live rather than analysed.
    private const int NearMissesToLog = 5;

    public static void Log(
        ILogger logger,
        int identitiesInRun, int comparisonSetSize,
        IReadOnlyList<FamilyDiagnostic> families,
        IReadOnlyList<SimilarityPair> nearMisses,
        ConfusableResult confusable,
        FamilyAssignment assignment)
    {
        foreach (var family in families)
            logger.LogInformation(
                "DocumentIdentityResolver: family {FamilyId} has {Size} members, weakest intra-family similarity {Weakest:F3}",
                family.FamilyId, family.Members.Count, family.WeakestSimilarity);

        // Anything other than Kept/Minted means a family's composition changed - the case that
        // used to rename families silently, and the one worth seeing in a live run.
        foreach (var decision in assignment.Decisions.Where(d => d.Kind is not FamilyAssignmentKind.Kept))
            logger.LogInformation(
                "DocumentIdentityResolver: family {FamilyId} {Kind} ({Members} member(s)){Detail}",
                decision.FamilyId, decision.Kind.ToString().ToLowerInvariant(), decision.Members.Count,
                decision.Detail is null ? "" : $" - {decision.Detail}");

        foreach (var pair in nearMisses.Take(NearMissesToLog))
            logger.LogInformation(
                "DocumentIdentityResolver: near miss - {SourceIdA} ~ {SourceIdB} at {Similarity:F3}, below the {Threshold:F2} threshold",
                pair.SourceIdA, pair.SourceIdB, pair.Similarity, CosineSimilarityClusterer.SimilarityThreshold);

        foreach (var match in confusable.Matches)
            logger.LogInformation(
                "DocumentIdentityResolver: confusable - {SourceId} vs {OtherSourceId} on '{Word}'/'{OtherWord}'",
                match.SourceId, match.OtherSourceId, match.Word, match.OtherWord);

        logger.LogInformation(
            "DocumentIdentityResolver: resolved {Identities} document(s) against a comparison set of {ComparisonSet}, " +
            "producing {Families} multi-member famil{FamilySuffix}, {NearMisses} near-miss pair(s) and {Confusable} confusable relation(s)",
            identitiesInRun, comparisonSetSize, families.Count,
            families.Count == 1 ? "y" : "ies",
            nearMisses.Count, confusable.Matches.Count);
    }
}
