using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Observability.Tests.Reports;

// The storage breakdown (2026-09-15). Graph overhead here is a SUBTRACTION, not a reported
// figure - the service publishes one combined vectorIndexSize - so these tests pin what that
// subtraction does at its edges, including where it goes negative.
[TestClass]
public class VectorStorageMetricsTests
{
    private const int Dims = 3072;   // 3,072 x 4 = 12,288 bytes per vector

    [TestMethod]
    public void BytesPerVector_IsDimensionsTimesFourBytes()
    {
        var m = VectorStorageMetrics.From(Dims, false, chunksProduced: 3_940, null, null, 0)!;

        Assert.AreEqual(12_288, m.BytesPerVector);
        Assert.AreEqual(3_940L * 12_288, m.RawVectorBytes);   // ~48 MB
    }

    [TestMethod]
    public void GraphOverhead_IsTheLiveSizeMinusTheRawVectors()
    {
        var raw = 3_940L * 12_288;
        var m = VectorStorageMetrics.From(Dims, false, 3_940, vectorIndexSizeBytes: raw + 4_000_000, null, 0)!;

        Assert.AreEqual(4_000_000L, m.GraphOverheadBytes);
        Assert.AreEqual(4_000_000 / (double)raw, m.GraphOverheadRatio!.Value, 1e-9);
    }

    [TestMethod]
    public void GraphOverhead_StaysNegativeRatherThanBeingClamped()
    {
        // Azure Search stats trail live writes by minutes, so a snapshot taken right after upload
        // can report a vector size smaller than the vectors just written. Clamping to zero would
        // hide exactly the staleness that makes the number untrustworthy - a negative reads as
        // "this stats sample is behind the upload", which is the true statement.
        var m = VectorStorageMetrics.From(Dims, false, 3_940, vectorIndexSizeBytes: 1_000, null, 0)!;

        Assert.IsTrue(m.GraphOverheadBytes < 0);
    }

    [TestMethod]
    public void NoVectorIndexSizeReported_LeavesOverheadNull_ButStillReportsRawBytes()
    {
        // The service not reporting vectorIndexSize must not blank the half we can compute.
        var m = VectorStorageMetrics.From(Dims, false, 3_940, vectorIndexSizeBytes: null, null, 0)!;

        Assert.IsNull(m.GraphOverheadBytes);
        Assert.IsNull(m.GraphOverheadRatio);
        Assert.AreEqual(3_940L * 12_288, m.RawVectorBytes);
    }

    [TestMethod]
    public void DeadWeight_IsCountedFromExistingGroundedCounts_NotAnInventedEmptinessRule()
    {
        // thin = below the chunking budget's own MinBodyTokenBudget floor; duplicate = repeated
        // content hash. Both already exist as measurements; neither is a threshold invented here.
        var m = VectorStorageMetrics.From(Dims, false, 3_940, null, thinChunks: 315, duplicateChunks: 39)!;

        Assert.AreEqual(315L * 12_288, m.ThinChunkBytes);       // ~3.9 MB
        Assert.AreEqual(39L  * 12_288, m.DuplicateChunkBytes);  // ~0.5 MB
    }

    [TestMethod]
    public void ThinChunksUnmeasured_IsNullNotZeroBytes()
    {
        var m = VectorStorageMetrics.From(Dims, false, 3_940, null, thinChunks: null, duplicateChunks: 0)!;

        Assert.IsNull(m.ThinChunks);
        Assert.IsNull(m.ThinChunkBytes);
    }

    [TestMethod]
    public void WithoutALiveWidth_NothingIsComputable()
    {
        // The width comes from the LIVE index, which can be absent (the readback failed). Without
        // it there is no bytes-per-vector, and guessing one from configuration would report bytes
        // the index does not actually hold - EnsureIndexAsync is get-or-create, so the two can
        // disagree indefinitely.
        Assert.IsNull(VectorStorageMetrics.From(null, false, 3_940, 1_000, null, 0));
        Assert.IsNull(VectorStorageMetrics.From(0,    false, 3_940, 1_000, null, 0));
    }

    [TestMethod]
    public void CompressionConfigured_IsCarriedSoOverheadIsNotMisread()
    {
        // With quantization on, RawVectorBytes is what the vectors WOULD take uncompressed, so
        // the gap to the live size stops being graph overhead alone. The flag is what tells a
        // reader that.
        var m = VectorStorageMetrics.From(Dims, compressionConfigured: true, 3_940, 1_000_000, null, 0)!;

        Assert.IsTrue(m.CompressionConfigured);
    }
}
