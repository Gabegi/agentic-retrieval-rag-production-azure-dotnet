using System.Text;

namespace RagApp.Evaluation.Tests.Evaluation;

// The deterministic retrieval metrics, as pure functions (2026-09-15). Pulled out of
// RagEvaluator so they can be unit-tested without a judge model or a knowledge base.
//
// The golden set labels DOCUMENTS (TestQuery.ExpectedSources, semicolon-separated PDF filenames
// matching Citation.DocumentId), not chunks, so every metric here is document-level: a retrieved
// reference is relevant when its document is one of the expected ones.
//
// Both sides are Unicode-normalized to NFC before comparing: source PDF filenames on disk can
// carry a decomposed diaeresis (e + combining U+0308, "cliënten") while the dataset is typed
// with the precomposed form (U+00EB) - OrdinalIgnoreCase does not normalize, so without this a
// correct citation for any such filename would silently score as a miss.
//
// Two label sets since 2026-09-23 (D225 §3 item 2, D228 step 1). ExpectedSources is ALL-OF:
// each id is one expected document. EquivalentSources is ANY-OF: the whole family is one
// expected document, satisfied by retrieving any member. "Wat is WARR?" has 81 documents that
// state the answer (D227 §4); listing them in ExpectedSources would demand all 81 and score a
// correct answer 0.012. So the denominator is expected.Count + (equivalent.Count > 0 ? 1 : 0),
// the numerator adds 1 when any equivalent id was retrieved, and a row is not scorable (-1)
// only when both sets are empty. A blank EquivalentSources leaves every metric exactly as it
// was: the two-argument overloads below delegate with "" and exist so callers and tests that
// predate the field keep compiling.
public static class RetrievalRankMetrics
{
    // Fraction of expected documents that appear among the cited/retrieved ones - document-level
    // recall over the retrieved set. -1 (not scorable) when ExpectedSources is empty, e.g. a
    // Refusal scenario or an "Onbekend" known-gap scenario.
    public static double CitationMatch(string expectedSources, IEnumerable<string> retrievedDocumentIds) =>
        CitationMatch(expectedSources, "", retrievedDocumentIds);

    public static double CitationMatch(string expectedSources, string equivalentSources, IEnumerable<string> retrievedDocumentIds)
    {
        var expected   = ExpectedIds(expectedSources);
        var equivalent = ExpectedIds(equivalentSources);
        if (expected.Count == 0 && equivalent.Count == 0) return -1;

        var retrieved = retrievedDocumentIds.Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hits  = expected.Count(retrieved.Contains) + (equivalent.Any(retrieved.Contains) ? 1 : 0);
        var total = expected.Count + (equivalent.Count > 0 ? 1 : 0);
        return hits / (double)total;
    }

    // 1-based rank of the first retrieved reference whose document is expected, in the ranking
    // the caller passes (RagQueryResult.RetrievedDocumentRanking: the service's reranker order).
    // 0 = none of the expected documents was retrieved at all; -1 = not scorable (no expected
    // sources, or the result carries no ranking - a guard-blocked row, or a result from before
    // the field existed).
    public static int FirstRelevantRank(string expectedSources, IReadOnlyList<string>? ranking) =>
        FirstRelevantRank(expectedSources, "", ranking);

    // A reference is relevant when its document is in EITHER set: the first expected or the
    // first equivalent document, whichever the reranker put higher.
    public static int FirstRelevantRank(string expectedSources, string equivalentSources, IReadOnlyList<string>? ranking)
    {
        var relevant = ExpectedIds(expectedSources);
        relevant.UnionWith(ExpectedIds(equivalentSources));
        if (relevant.Count == 0 || ranking is null) return -1;

        for (var i = 0; i < ranking.Count; i++)
            if (relevant.Contains(Normalize(ranking[i]))) return i + 1;
        return 0;
    }

    // Document-level recall@k: of the documents ExpectedSources names, the fraction that appear
    // among the first k retrieved references. The cutoff counts REFERENCES, not distinct
    // documents - k is the same unit as ReferencesRetrieved - so "recall@5" reads as "was the
    // right document somewhere in the first 5 things retrieval returned".
    //
    // Why both @5 and @50 (chunking-evaluations.md, D111): the GAP between them separates cut
    // from rank. A document in the top 50 but not the top 5 is a ranking problem; a document in
    // neither was never retrieved at all, which is a chunking/indexing problem no reranker can
    // fix. Either number alone cannot tell those apart, which is why both columns are reported.
    //
    // One degenerate case, expected rather than a bug: the production path sets no top-k
    // (AgenticRagQueryService passes none - the knowledge base decides how many references come
    // back), so when fewer than k references are returned, recall@k is recall over the WHOLE
    // returned set and @50 collapses onto it. Read it next to the mean ReferencesRetrieved the
    // summary prints beside it: if that mean is below 50, the @50 column is containment, not rank.
    //
    // -1 (not scorable) on the same rule as the others: no expected sources, or no ranking.
    public static double RecallAt(string expectedSources, IReadOnlyList<string>? ranking, int k) =>
        RecallAt(expectedSources, "", ranking, k);

    // Same denominator as CitationMatch: the equivalent family is one expected document, found
    // when any member is in the top k.
    public static double RecallAt(string expectedSources, string equivalentSources, IReadOnlyList<string>? ranking, int k)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(k, 1);

        var expected   = ExpectedIds(expectedSources);
        var equivalent = ExpectedIds(equivalentSources);
        if ((expected.Count == 0 && equivalent.Count == 0) || ranking is null) return -1;

        var topK  = ranking.Take(k).Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hits  = expected.Count(topK.Contains) + (equivalent.Any(topK.Contains) ? 1 : 0);
        var total = expected.Count + (equivalent.Count > 0 ? 1 : 0);
        return hits / (double)total;
    }

    // The per-row term of MRR. Averaged over scorable rows (>= 0) it is mean reciprocal rank:
    // 1.0 = the right document was always first, 0.5 = typically second, 0 = never retrieved.
    // Unlike CitationMatch it says how HIGH the right document sat, not just whether it was in
    // the window. -1 passes through so the aggregators' "not scored" filter excludes it.
    public static double ReciprocalRank(int firstRelevantRank) => firstRelevantRank switch
    {
        < 0 => -1,
        0   => 0,
        _   => 1.0 / firstRelevantRank,
    };

    private static HashSet<string> ExpectedIds(string expectedSources) =>
        expectedSources.Split(';')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Select(Normalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string Normalize(string value) => value.Normalize(NormalizationForm.FormC);
}
