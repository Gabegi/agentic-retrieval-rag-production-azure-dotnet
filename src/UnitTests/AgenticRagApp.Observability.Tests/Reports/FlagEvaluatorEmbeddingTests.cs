using AgenticRagApp.Observability.Reports;

namespace RagApp.UnitTests.Observability;

// The "did every chunk actually get embedded and land" checks (2026-09-15). Before these, the
// only embed-stage Criticals were DocsFailed and VectorDimErrors: a run that lost chunks
// between chunker and index with no upload error, or that indexed all-zero vectors, read as
// clean. These pin the evaluation, not the data.
[TestClass]
public class FlagEvaluatorEmbeddingTests
{
    private static EmbedUploadStageMetrics Embedding(int uploaded, int failed = 0, int? emptyVectors = 0) => new(
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
}
