using RagApp.Evaluation.Tests.Evaluation;

namespace RagApp.Evaluation.Tests;

// Offline - no judge, no knowledge base. Pins the document-level rank metrics (2026-09-15) and
// the sentinel rules the aggregators (eval-summary.jq, RunReportAssembler.Mean) depend on:
// -1 is excluded from every mean, 0 is a real miss and counts.
[TestClass]
public class RetrievalRankMetricsTests
{
    [TestMethod]
    public void FirstRelevantRank_IsTheOneBasedPositionOfTheFirstExpectedDocument()
    {
        var ranking = new[] { "other.pdf", "CAO GGZ.pdf", "CAO VVT.pdf" };

        Assert.AreEqual(2, RetrievalRankMetrics.FirstRelevantRank("CAO GGZ.pdf; CAO VVT.pdf", ranking));
        Assert.AreEqual(0.5, RetrievalRankMetrics.ReciprocalRank(2));
    }

    [TestMethod]
    public void FirstRelevantRank_RightDocumentFirst_IsOne()
    {
        Assert.AreEqual(1,   RetrievalRankMetrics.FirstRelevantRank("a.pdf", ["a.pdf", "b.pdf"]));
        Assert.AreEqual(1.0, RetrievalRankMetrics.ReciprocalRank(1));
    }

    [TestMethod]
    public void FirstRelevantRank_NoneOfTheExpectedDocumentsRetrieved_IsZero_AndCounts()
    {
        // A miss is a score of 0, not "not scored": it has to drag the mean down.
        Assert.AreEqual(0,   RetrievalRankMetrics.FirstRelevantRank("a.pdf", ["b.pdf", "c.pdf"]));
        Assert.AreEqual(0.0, RetrievalRankMetrics.ReciprocalRank(0));
    }

    [TestMethod]
    public void FirstRelevantRank_NotScorable_IsMinusOne()
    {
        // No expected sources (a Refusal or known-gap row), or no ranking on the result
        // (guard-blocked, or a result from before the field existed) - excluded from means.
        Assert.AreEqual(-1,   RetrievalRankMetrics.FirstRelevantRank("", ["a.pdf"]));
        Assert.AreEqual(-1,   RetrievalRankMetrics.FirstRelevantRank("a.pdf", null));
        Assert.AreEqual(-1.0, RetrievalRankMetrics.ReciprocalRank(-1));
    }

    [TestMethod]
    public void FirstRelevantRank_NormalizesToNfc_LikeCitationMatch()
    {
        // Decomposed diaeresis on the retrieved side (as filenames on disk carry it), precomposed
        // in the dataset. Same rule as CitationMatch, or the two metrics would disagree on the
        // same row.
        var decomposed  = "cliënten.pdf";
        var precomposed = "cliënten.pdf";

        Assert.AreEqual(1, RetrievalRankMetrics.FirstRelevantRank(precomposed, [decomposed]));
        Assert.AreEqual(1.0, RetrievalRankMetrics.CitationMatch(precomposed, [decomposed]));
    }

    [TestMethod]
    public void CitationMatch_IsDocumentLevelRecallOverTheRetrievedSet()
    {
        Assert.AreEqual(0.5, RetrievalRankMetrics.CitationMatch("a.pdf; b.pdf", ["a.pdf", "c.pdf", "a.pdf"]));
        Assert.AreEqual(-1,  RetrievalRankMetrics.CitationMatch("", ["a.pdf"]));
    }

    [TestMethod]
    public void RecallAt_CountsExpectedDocumentsInsideTheCutoff()
    {
        // b.pdf sits at rank 6, so it is outside @5 and inside @50: one of two expected
        // documents found at k=5, both at k=50.
        var ranking = new[] { "a.pdf", "x.pdf", "x.pdf", "y.pdf", "z.pdf", "b.pdf" };

        Assert.AreEqual(0.5, RetrievalRankMetrics.RecallAt("a.pdf; b.pdf", ranking, 5));
        Assert.AreEqual(1.0, RetrievalRankMetrics.RecallAt("a.pdf; b.pdf", ranking, 50));
    }

    [TestMethod]
    public void RecallAt_CutoffCountsReferencesNotDistinctDocuments()
    {
        // Five references, one distinct document, none of them expected: a duplicate-heavy
        // window does not buy extra room under the cutoff.
        var ranking = new[] { "x.pdf", "x.pdf", "x.pdf", "x.pdf", "x.pdf", "a.pdf" };

        Assert.AreEqual(0.0, RetrievalRankMetrics.RecallAt("a.pdf", ranking, 5));
        Assert.AreEqual(1.0, RetrievalRankMetrics.RecallAt("a.pdf", ranking, 6));
    }

    [TestMethod]
    public void RecallAt_FewerReferencesThanK_IsRecallOverTheWholeSet()
    {
        // The production path sets no top-k, so this is the normal case, not an edge case:
        // with 2 references returned, @50 is containment over everything retrieved.
        var ranking = new[] { "a.pdf", "x.pdf" };

        Assert.AreEqual(0.5, RetrievalRankMetrics.RecallAt("a.pdf; b.pdf", ranking, 50));
    }

    [TestMethod]
    public void RecallAt_NotScorable_IsMinusOne()
    {
        Assert.AreEqual(-1, RetrievalRankMetrics.RecallAt("", ["a.pdf"], 5));
        Assert.AreEqual(-1, RetrievalRankMetrics.RecallAt("a.pdf", null, 5));
    }

    [TestMethod]
    public void RecallAt_NormalizesToNfc_LikeTheOtherMetrics()
    {
        Assert.AreEqual(1.0, RetrievalRankMetrics.RecallAt("cliënten.pdf", ["cliënten.pdf"], 5));
    }

    [TestMethod]
    public void RecallAt_RejectsANonPositiveCutoff()
    {
        // A k of 0 would silently score every row 0.0 and read as total retrieval failure.
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => RetrievalRankMetrics.RecallAt("a.pdf", ["a.pdf"], 0));
    }

    [TestMethod]
    public void Invariants_RecallAt50AtLeastRecallAt5_AndMrrPositiveWheneverRecallAt50Is()
    {
        // The two invariants chunking-evaluations.md (D111) asks to assert, over a spread of
        // rankings: a wider window can only find more, and if the window found the document at
        // all then some rank exists, so the reciprocal rank cannot be zero.
        string[][] rankings =
        [
            ["a.pdf", "b.pdf"],
            ["x.pdf", "y.pdf", "z.pdf", "w.pdf", "v.pdf", "a.pdf"],
            ["x.pdf", "y.pdf"],
            [],
        ];

        foreach (var ranking in rankings)
        {
            const string expected = "a.pdf; b.pdf";

            var r5  = RetrievalRankMetrics.RecallAt(expected, ranking, 5);
            var r50 = RetrievalRankMetrics.RecallAt(expected, ranking, 50);
            var rr  = RetrievalRankMetrics.ReciprocalRank(
                RetrievalRankMetrics.FirstRelevantRank(expected, ranking));

            Assert.IsTrue(r50 >= r5, $"recall@50 ({r50}) < recall@5 ({r5}) for [{string.Join(", ", ranking)}]");
            if (r50 > 0)
                Assert.IsTrue(rr > 0, $"recall@50 is {r50} but reciprocal rank is {rr} for [{string.Join(", ", ranking)}]");
        }
    }

    [TestMethod]
    public void RankAndRecall_DisagreeExactlyWhereTheyShould()
    {
        // Both expected documents retrieved (recall 1.0) but the first one only at rank 4:
        // CitationMatch cannot see that, the reciprocal rank can.
        var ranking = new[] { "x.pdf", "y.pdf", "z.pdf", "a.pdf", "b.pdf" };

        Assert.AreEqual(1.0,  RetrievalRankMetrics.CitationMatch("a.pdf; b.pdf", ranking));
        Assert.AreEqual(0.25, RetrievalRankMetrics.ReciprocalRank(RetrievalRankMetrics.FirstRelevantRank("a.pdf; b.pdf", ranking)));
    }

    // ---- EquivalentSources: the any-of family (2026-09-23, D228 step 1) -------------------

    [TestMethod]
    public void EquivalentSources_AnyMemberRetrieved_CountsAsTheOneExpectedDocument()
    {
        // "Wat is WARR?": 81 documents state the answer. Retrieving one of them is a full hit.
        var ranking = new[] { "other.pdf", "plan-b.pdf", "plan-c.pdf" };

        Assert.AreEqual(1.0, RetrievalRankMetrics.CitationMatch("", "plan-a.pdf; plan-b.pdf; plan-c.pdf", ranking));
        Assert.AreEqual(2,   RetrievalRankMetrics.FirstRelevantRank("", "plan-a.pdf; plan-b.pdf; plan-c.pdf", ranking));
        Assert.AreEqual(1.0, RetrievalRankMetrics.RecallAt("", "plan-a.pdf; plan-b.pdf; plan-c.pdf", ranking, 5));
        Assert.AreEqual(0.0, RetrievalRankMetrics.RecallAt("", "plan-a.pdf; plan-b.pdf; plan-c.pdf", ranking, 1));
    }

    [TestMethod]
    public void EquivalentSources_TwoMembersRetrieved_StillCountOnce()
    {
        // Any-of, not all-of and not a bonus: the family is one document however many came back.
        Assert.AreEqual(1.0, RetrievalRankMetrics.CitationMatch("", "a.pdf; b.pdf", ["a.pdf", "b.pdf"]));
        Assert.AreEqual(1.0, RetrievalRankMetrics.RecallAt("", "a.pdf; b.pdf", ["a.pdf", "b.pdf"], 5));
    }

    [TestMethod]
    public void EquivalentSources_BesideExpected_IsOneMoreExpectedDocument()
    {
        // gq-vocab-003 in D228's recommended form: the policy in ExpectedSources, the 80 plans as
        // equivalents. A plan-only retrieval is half right; the policy alone is half right too.
        Assert.AreEqual(0.5, RetrievalRankMetrics.CitationMatch("policy.pdf", "plan-a.pdf; plan-b.pdf", ["plan-b.pdf"]));
        Assert.AreEqual(0.5, RetrievalRankMetrics.CitationMatch("policy.pdf", "plan-a.pdf; plan-b.pdf", ["policy.pdf"]));
        Assert.AreEqual(1.0, RetrievalRankMetrics.CitationMatch("policy.pdf", "plan-a.pdf; plan-b.pdf", ["policy.pdf", "plan-a.pdf"]));
        Assert.AreEqual(0.0, RetrievalRankMetrics.CitationMatch("policy.pdf", "plan-a.pdf; plan-b.pdf", ["other.pdf"]));

        // Rank is the first document from EITHER set.
        Assert.AreEqual(1, RetrievalRankMetrics.FirstRelevantRank("policy.pdf", "plan-a.pdf", ["plan-a.pdf", "policy.pdf"]));
        Assert.AreEqual(2, RetrievalRankMetrics.FirstRelevantRank("policy.pdf", "plan-a.pdf", ["other.pdf", "policy.pdf", "plan-a.pdf"]));

        // Recall@k uses the same denominator as CitationMatch.
        Assert.AreEqual(0.5, RetrievalRankMetrics.RecallAt("policy.pdf", "plan-a.pdf", ["plan-a.pdf", "other.pdf", "policy.pdf"], 2));
        Assert.AreEqual(1.0, RetrievalRankMetrics.RecallAt("policy.pdf", "plan-a.pdf", ["plan-a.pdf", "other.pdf", "policy.pdf"], 3));
    }

    [TestMethod]
    public void EquivalentSources_BlankLeavesEveryMetricUnchanged()
    {
        // Every golden row today has the field blank, so the three-argument overloads must equal
        // the two-argument ones on every input - this is the "zero rows move" guarantee.
        string[] ranking = ["b.pdf", "a.pdf", "c.pdf"];
        foreach (var equivalent in new[] { "", "  ", ";", " ; " })
        {
            Assert.AreEqual(RetrievalRankMetrics.CitationMatch("a.pdf; c.pdf", ranking),
                            RetrievalRankMetrics.CitationMatch("a.pdf; c.pdf", equivalent, ranking));
            Assert.AreEqual(RetrievalRankMetrics.FirstRelevantRank("a.pdf; c.pdf", ranking),
                            RetrievalRankMetrics.FirstRelevantRank("a.pdf; c.pdf", equivalent, ranking));
            Assert.AreEqual(RetrievalRankMetrics.RecallAt("a.pdf; c.pdf", ranking, 2),
                            RetrievalRankMetrics.RecallAt("a.pdf; c.pdf", equivalent, ranking, 2));
            Assert.AreEqual(-1.0, RetrievalRankMetrics.CitationMatch("", equivalent, ranking));
            Assert.AreEqual(-1,   RetrievalRankMetrics.FirstRelevantRank("", equivalent, ranking));
            Assert.AreEqual(-1.0, RetrievalRankMetrics.RecallAt("", equivalent, ranking, 5));
        }
    }

    [TestMethod]
    public void EquivalentSources_NotScorableRules_MatchExpectedSources()
    {
        // Only-equivalent rows are scorable; a null ranking is not, whichever set is filled.
        Assert.AreEqual(0.0, RetrievalRankMetrics.CitationMatch("", "a.pdf", ["b.pdf"]));
        Assert.AreEqual(0,   RetrievalRankMetrics.FirstRelevantRank("", "a.pdf", ["b.pdf"]));
        Assert.AreEqual(-1,  RetrievalRankMetrics.FirstRelevantRank("", "a.pdf", null));
        Assert.AreEqual(-1.0, RetrievalRankMetrics.RecallAt("", "a.pdf", null, 5));
    }

    [TestMethod]
    public void EquivalentSources_NormalizesToNfc_LikeExpectedSources()
    {
        var decomposed  = "clie\u0308nten.pdf";
        var precomposed = "cli\u00ebnten.pdf";

        Assert.AreEqual(1.0, RetrievalRankMetrics.CitationMatch("", precomposed, [decomposed]));
        Assert.AreEqual(1,   RetrievalRankMetrics.FirstRelevantRank("", precomposed, [decomposed]));
        Assert.AreEqual(1.0, RetrievalRankMetrics.RecallAt("", precomposed, [decomposed], 1));
    }
}
