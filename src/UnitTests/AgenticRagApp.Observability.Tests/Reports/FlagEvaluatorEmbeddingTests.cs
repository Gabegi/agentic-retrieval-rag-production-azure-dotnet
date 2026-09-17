using AgenticRagApp.Observability.Reports;

namespace RagApp.UnitTests.Observability;

// The "did every chunk actually get embedded and land" checks (2026-09-15). Before these, the
// only embed-stage Criticals were DocsFailed and VectorDimErrors: a run that lost chunks
// between chunker and index with no upload error, or that indexed all-zero vectors, read as
// clean. These pin the evaluation, not the data.
[TestClass]
public class FlagEvaluatorEmbeddingTests
{
    private static EmbedUploadStageMetrics Embedding(
        int uploaded, int failed = 0, int? emptyVectors = 0, int? withheld = null) => new(
        DocsUploaded:                  uploaded,
        DocsFailed:                    failed,
        ChunksRemoved:                 0,
        ChunkFamiliesPatched:          0,
        ChunksTruncated:               0,
        EmbeddingRetries:              0,
        VectorDimErrors:               0,
        VectorCacheHits:               0,
        TotalEmbeddingDurationMs:      0,
        IndexDocumentCountSnapshot:    null,
        IndexStorageSizeBytesSnapshot: null,
        RedFlags:                      [],
        ChunksEvicted:                 0,
        PreviousIndexDocumentCount:    null,
        PreviousIndexStorageSizeBytes: null)
    {
        EmptyVectors = emptyVectors,
        DocsWithheld = withheld,
    };

    private static PdfIndexRunReport Report(int chunksProduced, EmbedUploadStageMetrics? embedding) => new()
    {
        Run = new RunIdentity(
            InstanceId:   "test-instance",
            StartedAt:    DateTimeOffset.UnixEpoch,
            FinishedAt:   DateTimeOffset.UnixEpoch,
            ForceReindex: true,
            Success:      true,
            ErrorMessage: null),
        Chunking  = ChunkingStageMetrics.Empty("TwoAxisChunking") with { ChunksProduced = chunksProduced },
        Embedding = embedding,
    };

    private static IReadOnlyList<ReportFlag> Evaluate(PdfIndexRunReport report) =>
        FlagEvaluator.Evaluate(report, fileFacts: null, previous: null, calibrationMode: false);

    // ── Embedding.DocsUploaded: uploaded + failed == produced ─────────────────────────────

    [TestMethod]
    public void EveryChunkAccountedFor_ProducesNoUploadFlag()
    {
        var flags = Evaluate(Report(chunksProduced: 3940, Embedding(uploaded: 3940)));

        Assert.IsFalse(flags.Any(f => f.Metric == "Embedding.DocsUploaded"));
    }

    [TestMethod]
    public void UploadedPlusFailedEqualToProduced_ProducesNoUploadFlag()
    {
        // The failures are DocsFailed's flag, not this one's - the accounting still adds up.
        var flags = Evaluate(Report(chunksProduced: 3940, Embedding(uploaded: 3930, failed: 10)));

        Assert.IsFalse(flags.Any(f => f.Metric == "Embedding.DocsUploaded"));
        Assert.IsTrue(flags.Any(f => f.Metric == "Embedding.DocsFailed"));
    }

    [TestMethod]
    public void ShortfallWithoutFailures_IsCritical()
    {
        var flags = Evaluate(Report(chunksProduced: 3940, Embedding(uploaded: 3900)));

        var flag = flags.SingleOrDefault(f => f.Metric == "Embedding.DocsUploaded");
        Assert.IsNotNull(flag, "40 chunks vanished with no error and nothing said so");
        Assert.AreEqual(FlagSeverity.Critical, flag!.Severity);
        StringAssert.Contains(flag.Observed, "3900");
        StringAssert.Contains(flag.Expected, "3940");
        Assert.IsFalse(flag.AwaitingCalibration, "an identity has nothing to calibrate");
    }

    [TestMethod]
    public void MoreUploadedThanProduced_IsCritical()
    {
        // Double counting is the same defect from the other side.
        var flags = Evaluate(Report(chunksProduced: 3940, Embedding(uploaded: 3950)));

        Assert.IsTrue(flags.Any(f => f.Metric == "Embedding.DocsUploaded" && f.Severity == FlagSeverity.Critical));
    }

    [TestMethod]
    public void NullChunkingStage_ProducesNoUploadFlag()
    {
        // Nothing to reconcile against - absence of measurement is not a failing measurement.
        var report = Report(chunksProduced: 0, Embedding(uploaded: 3940)) with { Chunking = null };

        var flags = Evaluate(report);

        Assert.IsFalse(flags.Any(f => f.Metric == "Embedding.DocsUploaded"));
    }

    // ── Embedding.EmptyVectors ────────────────────────────────────────────────────────────

    [TestMethod]
    public void EmptyVectors_IsCritical()
    {
        var flags = Evaluate(Report(chunksProduced: 3940, Embedding(uploaded: 3940, emptyVectors: 3)));

        var flag = flags.SingleOrDefault(f => f.Metric == "Embedding.EmptyVectors");
        Assert.IsNotNull(flag);
        Assert.AreEqual(FlagSeverity.Critical, flag!.Severity);
        Assert.AreEqual("3", flag.Observed);
    }

    [TestMethod]
    public void ZeroEmptyVectors_ProducesNoFlag()
    {
        var flags = Evaluate(Report(chunksProduced: 3940, Embedding(uploaded: 3940, emptyVectors: 0)));

        Assert.IsFalse(flags.Any(f => f.Metric == "Embedding.EmptyVectors"));
    }

    [TestMethod]
    public void EmptyVectorsNotMeasured_ProducesNoFlag()
    {
        // Reports written before the counter existed read back as null. Null is not zero, and
        // it is not a defect either - it is no measurement.
        var flags = Evaluate(Report(chunksProduced: 3940, Embedding(uploaded: 3940, emptyVectors: null)));

        Assert.IsFalse(flags.Any(f => f.Metric == "Embedding.EmptyVectors"));
    }

    [TestMethod]
    public void NullEmbeddingStage_ProducesNoEmbeddingFlags()
    {
        var flags = Evaluate(Report(chunksProduced: 3940, embedding: null));

        Assert.IsFalse(flags.Any(f => f.Metric.StartsWith("Embedding.")));
    }

    // ── DocsFailed now folds two causes (D199 A3) ────────────────────────────

    private static ReportFlag DocsFailedFlag(IReadOnlyList<ReportFlag> flags) =>
        flags.Single(f => f.Metric == "Embedding.DocsFailed");

    // Nothing withheld - and a report that predates the field (null) must read exactly as it did
    // before, which is why the branch is on DocsWithheld and not on "is this a new report".
    [TestMethod]
    [DataRow(0,    "withheld counted as zero")]
    [DataRow(null, "report predates DocsWithheld")]
    public void DocsFailed_NothingWithheld_KeepsTheOriginalWording(int? withheld, string label)
    {
        var flags = Evaluate(Report(3940, Embedding(uploaded: 3938, failed: 2, withheld: withheld)));
        var flag  = DocsFailedFlag(flags);

        Assert.AreEqual("Those chunks are silently missing from the index.", flag.Meaning, label);
        StringAssert.Contains(flag.Action, "throttling", label);
    }

    // With chunks withheld, the split is STATED - the reader gets both numbers rather than being
    // told the count "includes some" of each.
    [TestMethod]
    public void DocsFailed_SomeWithheld_StatesTheSplitAndDropsTheSilentlyClaim()
    {
        var flags = Evaluate(Report(3940, Embedding(uploaded: 3937, failed: 3, emptyVectors: 2, withheld: 2)));
        var flag  = DocsFailedFlag(flags);

        // 3 failed, 2 of them withheld, so 1 was refused by Search.
        StringAssert.Contains(flag.Meaning, "1 refused by Search");
        StringAssert.Contains(flag.Meaning, "2 withheld");

        // The blanket "silently missing" claim must not stand alone once some were logged.
        Assert.AreNotEqual("Those chunks are silently missing from the index.", flag.Meaning);

        // A2's gap is visible here or nowhere: this is the only place a reader learns that some
        // withheld chunks come back by themselves and some do not.
        StringAssert.Contains(flag.Meaning, "reading stale");
        StringAssert.Contains(flag.Meaning, "will not come back on its own");
    }

    // Upstream before re-running: a re-run ahead of the fix spends a Content Understanding
    // extraction to reach the same verdict.
    [TestMethod]
    public void DocsFailed_SomeWithheld_SequencesRemediationCauseFirst()
    {
        var flags = Evaluate(Report(3940, Embedding(uploaded: 3939, failed: 1, emptyVectors: 1, withheld: 1)));
        var flag  = DocsFailedFlag(flags);

        StringAssert.Contains(flag.Action, "Fix the cause before re-running");
        StringAssert.Contains(flag.Action, "OPENAI_EMBEDDING_DIMENSIONS");

        // This pins the ORDER - cause before re-run - not the phrasing. Reword the sentence freely
        // and update the two search strings with it; what must not change is which instruction
        // comes first, because a re-run ahead of the fix spends a paid Content Understanding
        // extraction to arrive at the same verdict. Do not delete this on a rewording.
        Assert.IsTrue(
            flag.Action.IndexOf("Fix the cause", StringComparison.Ordinal)
            < flag.Action.IndexOf("re-run indexing", StringComparison.OrdinalIgnoreCase),
            "the fix has to be named before the re-run, or the re-run is a wasted extraction");
    }

    // Every chunk in DocsFailed was withheld: nothing was refused, so the flag must not invent a
    // Search problem to blame.
    [TestMethod]
    public void DocsFailed_AllWithheld_ReportsZeroRefused()
    {
        var flags = Evaluate(Report(3940, Embedding(uploaded: 3938, failed: 2, emptyVectors: 2, withheld: 2)));

        StringAssert.Contains(DocsFailedFlag(flags).Meaning, "0 refused by Search");
    }
}
