namespace AgenticRagApp.Indexing.Pdf.Services;

// Union-find over cosine similarity - the family-grouping half of DocumentIdentityResolver, split out
// so the algorithm can be tested on plain vectors. O(n^2) pairwise comparisons, trivial at this
// corpus's scale (dozens to low hundreds of documents): ~1,275 comparisons at 51 documents,
// ~500k at 1,000. FamilyId is the lexicographically smallest SourceId in the cluster, so it is
// deterministic and traceable back to a real document rather than an opaque generated GUID.
//
// Note this is single-linkage: A~B and B~C merges A with C even when A and C are far apart.
// That is the expected over-merge failure mode at a fixed threshold, and at a threshold slightly
// too low it does not fail gracefully - it chains, pulling unrelated families into one blob.
// Diagnostics are returned rather than logged so the caller owns all output: the weakest link
// inside each family shows over-merging, and the near-miss pairs show what the threshold kept
// apart. Both halves are needed to calibrate the threshold; the weakest link alone can only
// ever answer "is this family too loose?".
public static class CosineSimilarityClusterer
{
    // Cosine similarity above which two documents are considered the same family.
    //
    // CALIBRATED 2026-08-14 against the full 51-document corpus (run 08:32, see
    // docs/2608/260814/calibration-findings.md §2). The previous 0.90 was a reasoned guess and
    // it was far too high: it produced ZERO multi-member families, which made family_id carry
    // no information at all and left the families.md §6 conflict rule unable to fire even in
    // principle.
    //
    // Every known Type A family scored between 0.770 and 0.880 - Handreiking LVB~MVB 0.880,
    // CAO GHZ~VVT 0.873, CAO GGZ~GHZ 0.790, CAO GGZ~VVT 0.786, the two verstrekkingen
    // brochures 0.770 - and the highest non-family pair (Privacybeleid ~ cameratoepassingen,
    // topical overlap rather than near-duplication) scored 0.753. 0.76 is the value that
    // captures all four families and excludes that pair. Single-linkage then does the right
    // thing: GGZ~GHZ at 0.790 chains GGZ into the {GHZ, VVT} pair, giving the whole CAO trio.
    //
    // The margin is thin - 0.017 between the last true positive and the first false positive -
    // so this is calibrated to THIS corpus, not a universal constant. Recheck it whenever the
    // corpus changes materially.
    public const double SimilarityThreshold = 0.76;

    // Pairs scoring below this are not worth reporting as near misses - they are simply
    // different documents, and including them would bury the interesting band in noise.
    //
    // Lowered 0.75 -> 0.60 with the threshold above (C2-a). At 0.75 the floor sat only 0.01
    // below the chosen threshold, so the run that had to justify the threshold could not see
    // what was underneath it. A wider band costs a few more log lines and shows whether the
    // gap under 0.76 is genuinely empty or merely unobserved.
    public const double NearMissFloor = 0.60;

    public static ClusterResult Cluster(IReadOnlyDictionary<string, float[]> vectorsById)
    {
        var ids    = vectorsById.Keys.ToList();
        var parent = ids.ToDictionary(id => id, id => id);

        string Find(string x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x         = parent[x];
            }
            return x;
        }

        void Union(string a, string b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra != rb) parent[ra] = rb;
        }

        // Sub-threshold scores are computed here anyway; keeping the ones above the floor costs
        // nothing and is the difference between the threshold being an answerable question and
        // a guess.
        var nearMisses = new List<SimilarityPair>();

        for (int i = 0; i < ids.Count; i++)
        {
            for (int j = i + 1; j < ids.Count; j++)
            {
                var similarity = CosineSimilarity(vectorsById[ids[i]], vectorsById[ids[j]]);

                if (similarity >= SimilarityThreshold)
                    Union(ids[i], ids[j]);
                else if (similarity >= NearMissFloor)
                    nearMisses.Add(new SimilarityPair(ids[i], ids[j], similarity));
            }
        }

        var familyIdOf  = new Dictionary<string, string>();
        var diagnostics = new List<FamilyDiagnostic>();

        foreach (var cluster in ids.GroupBy(Find))
        {
            var members  = cluster.OrderBy(id => id, StringComparer.Ordinal).ToList();
            var familyId = members[0];

            foreach (var member in members)
                familyIdOf[member] = familyId;

            if (members.Count == 1) continue;

            // Weakest intra-family pair, recomputed over this cluster's members only rather than
            // kept from the pass above: clusters are small, so this is far cheaper than holding
            // all n^2 similarities in memory for the sake of one diagnostic.
            var weakest = double.MaxValue;
            for (int i = 0; i < members.Count; i++)
                for (int j = i + 1; j < members.Count; j++)
                    weakest = Math.Min(
                        weakest,
                        CosineSimilarity(vectorsById[members[i]], vectorsById[members[j]]));

            diagnostics.Add(new FamilyDiagnostic(familyId, members, weakest));
        }

        return new ClusterResult(
            familyIdOf,
            diagnostics.OrderBy(d => d.FamilyId, StringComparer.Ordinal).ToList(),
            nearMisses.OrderByDescending(p => p.Similarity).ToList());
    }

    public static double CosineSimilarity(float[] a, float[] b)
    {
        if (a is null || b is null || a.Length != b.Length || a.Length == 0)
            return 0;

        double dot = 0, magA = 0, magB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot  += (double)a[i] * b[i];
            magA += (double)a[i] * a[i];
            magB += (double)b[i] * b[i];
        }

        return magA == 0 || magB == 0 ? 0 : dot / (Math.Sqrt(magA) * Math.Sqrt(magB));
    }
}

// One entry per multi-member family. Single-member families are omitted: every document that
// clustered with nothing would otherwise produce a diagnostic saying so.
public sealed record FamilyDiagnostic(string FamilyId, IReadOnlyList<string> Members, double WeakestSimilarity);

public sealed record SimilarityPair(string SourceIdA, string SourceIdB, double Similarity);

public sealed record ClusterResult(
    IReadOnlyDictionary<string, string> FamilyIdOf,
    IReadOnlyList<FamilyDiagnostic>     Families,
    IReadOnlyList<SimilarityPair>       NearMisses);
