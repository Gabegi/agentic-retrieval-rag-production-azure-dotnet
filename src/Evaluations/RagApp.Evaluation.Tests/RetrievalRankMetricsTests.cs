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
}
