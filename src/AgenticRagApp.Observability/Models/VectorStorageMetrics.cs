namespace AgenticRagApp.Observability.Reports;

// What the vectors cost to STORE, as opposed to what they cost to produce (2026-09-15).
//
// Read this as a step function, not a per-MB line: you pay for a search tier and its partitions,
// and the vector quota is in GB. A few dozen MB against that means storage is currently costing
// nothing - the value of these numbers is the trend and the composition, not the total.
//
// The prefix deliberately does not appear here. A vector is Dimensions floats wide regardless of
// what text produced it, so the prefix costs tokens (EmbeddingCostMetrics) and exactly zero index
// bytes. Keeping it out of this record is the point, not an omission.
public sealed record VectorStorageMetrics(
    // Vector width and the bytes one vector occupies uncompressed. float32, hence x4 - the
    // service stores float32 unless a compression profile says otherwise, which is why
    // CompressionConfigured rides along: with quantization on, RawVectorBytes is what the vectors
    // WOULD take, and the gap to VectorIndexSizeBytes stops being graph overhead alone.
    int      Dimensions,
    int      BytesPerVector,
    bool     CompressionConfigured,

    // ChunksProduced x BytesPerVector: the payload, before any index structure.
    long     RawVectorBytes,

    // The service's own figure for the vector field and its HNSW graph together - what counts
    // against the tier's vector quota. Null = the service did not report it.
    long?    VectorIndexSizeBytes,

    // VectorIndexSizeBytes - RawVectorBytes. A SUBTRACTION, not a reported field: the service
    // publishes one combined number, so the graph's cost is only ever inferred. It therefore
    // inherits every error in the document count and in the float32 assumption above, and can
    // come out negative when the stats read lags the upload (Azure Search stats trail live
    // writes by minutes) or when compression is on. Negative is left as-is rather than clamped -
    // a clamp would hide exactly the staleness that makes the number untrustworthy.
    long?    GraphOverheadBytes,
    double?  GraphOverheadRatio,

    // ── Dead weight ──────────────────────────────────────────────────────────────────────────
    // Index bytes spent on chunks that carry little or nothing. Both counts come from measures
    // that already exist and are grounded in something real - no invented "empty" threshold:
    //   thin      = Chunking.Tokens.UnderMinBodyBudget, i.e. below the floor the chunking budget
    //               itself sets for how much body a chunk must keep against its prefix. A chunk
    //               under it is mostly prefix by the budget's own definition.
    //   duplicate = Chunking.DuplicateChunks, chunks whose content hash repeats.
    // The two can overlap; they are reported separately rather than summed for that reason.
    int?     ThinChunks,
    long?    ThinChunkBytes,
    int      DuplicateChunks,
    long     DuplicateChunkBytes)
{
    private const int BytesPerFloat32 = 4;

    public static VectorStorageMetrics? From(
        int? dimensions,
        bool compressionConfigured,
        int chunksProduced,
        long? vectorIndexSizeBytes,
        int? thinChunks,
        int duplicateChunks)
    {
        // Without the width there is no bytes-per-vector and nothing here can be computed. The
        // width comes from the LIVE index definition (IndexVectorConfig.Dimensions), not from
        // configuration, so this reports what the index actually stores rather than what the code
        // asked for - the two can disagree indefinitely, because EnsureIndexAsync is get-or-create.
        if (dimensions is not { } dims || dims <= 0) return null;

        var bytesPerVector = (long)dims * BytesPerFloat32;
        var rawBytes       = bytesPerVector * chunksProduced;

        long?   overhead = vectorIndexSizeBytes is { } live ? live - rawBytes : null;
        double? ratio    = overhead is { } o && rawBytes > 0 ? o / (double)rawBytes : null;

        return new VectorStorageMetrics(
            Dimensions:            dims,
            BytesPerVector:        (int)bytesPerVector,
            CompressionConfigured: compressionConfigured,
            RawVectorBytes:        rawBytes,
            VectorIndexSizeBytes:  vectorIndexSizeBytes,
            GraphOverheadBytes:    overhead,
            GraphOverheadRatio:    ratio,
            ThinChunks:            thinChunks,
            ThinChunkBytes:        thinChunks is { } t ? bytesPerVector * t : null,
            DuplicateChunks:       duplicateChunks,
            DuplicateChunkBytes:   bytesPerVector * duplicateChunks);
    }
}
