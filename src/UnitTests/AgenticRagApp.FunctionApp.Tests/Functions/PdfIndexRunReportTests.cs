using AgenticRagApp.Common.Models;
using AgenticRagApp.Observability.Reports;

namespace RagApp.UnitTests.Functions;

// Replaces PdfIndexRunReportFromResultsTests. FromResults was a 45-line hand-copy that
// flattened three stage records into ~40 fields, substituting `?? 0` for any stage that
// never ran - it was the highest-complexity method in the assembly and needed tests for
// exactly that reason. The report now composes the stage records directly, so what's
// worth asserting is the composition and, above all, that an absent stage stays null
// rather than becoming a record full of zeroes.
[TestClass]
public class PdfIndexRunReportTests
{
    private static ExtractionStageMetrics Extraction(IReadOnlyList<string>? redFlags = null) => new(
        Source:                 "pdf",
        DocsToProcess:          1,
        DocsSkipped:            2,
        DocsNew:                1,
        DocsUpdated:            3,
        DocsDeleted:            4,
        StaleDocumentIds:       [],
        ValidationErrors:       5,
        ValidationWarnings:     6,
        ReconciliationProblems: 7,
        StaleDocCount:          null,
        MojibakeRepairedPages:  13,
        DetectedTableCount:     14,
        DocsWithoutHeadings:    9,
        MissingTitleCount:      10,
        MissingVersionCount:    null,
        MissingDepartmentCount: null,
        TraceabilityGapCount:   11,
        Issues:                 [PipelineIssue.Error(PipelineStage.ParsePages, "doc1", "bad row")],
        RedFlags:               redFlags ?? ["extraction flag"],
        SpotCheckSample:        [new SpotCheckEntry("doc1", "Title", "preview...")]);

    private static ChunkingStageMetrics Chunking() => new(
        ChunksProduced:     20,
        DocsWithZeroChunks: 1,
        DuplicateChunks:    2,
        MinChunkSizeChars:  50,
        MaxChunkSizeChars:  1500,
        AvgChunkSizeChars:  750.5,
        P95ChunkSizeChars:  1400,
        BandUnder100:       1,
        Band100To500:       2,
        Band500To1500:      15,
        Band1500Plus:       2,
        CoherentChunks:     18,
        HeadingsDetected:   19,
        Strategy:           "SentenceAwareSlidingWindow",
        ZeroChunkDocumentIds: ["doc-with-no-chunks.pdf"],
        SampleChunks:         [],
        SmallestChunk:        null,
        LargestChunk:         null,
        DuplicateSamples:     []);

    private static EmbedUploadStageMetrics EmbedUpload(IReadOnlyList<string>? redFlags = null) => new(
        DocsUploaded:                  20,
        DocsFailed:                    1,
        ChunksRemoved:                 5,
        ChunksTruncated:               2,
        EmbeddingRetries:              3,
        VectorDimErrors:               0,
        VectorCacheHits:               0,
        TotalEmbeddingDurationMs:      1234,
        IndexDocumentCountSnapshot:    1000,
        IndexStorageSizeBytesSnapshot: 2_000_000,
        RedFlags:                     redFlags ?? ["upload flag"],
        ChunksEvicted:                 7,
        PreviousIndexDocumentCount:    950,
        PreviousIndexStorageSizeBytes: 1_900_000);

    private static PdfIndexRunReport Build(
        ExtractionStageMetrics? ext = null,
        ChunkingStageMetrics? chunk = null,
        EmbedUploadStageMetrics? embed = null,
        bool success = true,
        string? error = null,
        bool forceReindex = false,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? finishedAt = null) => new()
        {
            Run = new RunIdentity(
                "instance-1",
                startedAt  ?? DateTimeOffset.Parse("2024-01-01T00:00:00Z"),
                finishedAt ?? DateTimeOffset.Parse("2024-01-01T01:00:00Z"),
                forceReindex,
                success,
                error),
            Extraction = ext,
            Chunking   = chunk,
            Embedding  = embed,
        };

    [TestMethod]
    public void AllStagesPresent_ExposesEachStagesOwnNumbers()
    {
        var report = Build(Extraction(), Chunking(), EmbedUpload(), forceReindex: true);

        Assert.AreEqual("instance-1", report.InstanceId);
        Assert.AreEqual(DateTimeOffset.Parse("2024-01-01T00:00:00Z"), report.Run.StartedAt);
        Assert.AreEqual(DateTimeOffset.Parse("2024-01-01T01:00:00Z"), report.Run.FinishedAt);
        Assert.IsTrue(report.Run.ForceReindex);
        Assert.IsTrue(report.Success);
        Assert.IsNull(report.ErrorMessage);

        Assert.AreEqual(1, report.Extraction!.DocsToProcess);
        Assert.AreEqual(20, report.Chunking!.ChunksProduced);
        Assert.AreEqual(20, report.Embedding!.DocsUploaded);
        Assert.AreEqual(1000L, report.Embedding.IndexDocumentCountSnapshot);
        Assert.AreEqual(2_000_000L, report.Embedding.IndexStorageSizeBytesSnapshot);
        Assert.AreEqual(1, report.Extraction.Issues.Count);
        Assert.AreEqual(1, report.Extraction.SpotCheckSample.Count);
        Assert.AreEqual(11, report.TraceabilityGapCount);
    }

    // The whole point of the composed shape. Under the old FromResults these fields came
    // back as 0, indistinguishable from a stage that ran and measured nothing.
    [TestMethod]
    public void StagesThatNeverRan_AreNull_NotZeroed()
    {
        var report = Build(success: false, error: "boom");

        Assert.IsFalse(report.Success);
        Assert.AreEqual("boom", report.ErrorMessage);
        Assert.IsNull(report.Extraction);
        Assert.IsNull(report.Chunking);
        Assert.IsNull(report.Embedding);
        Assert.IsNull(report.TraceabilityGapCount);
    }

    [TestMethod]
    public void OnlyExtractionRan_LaterStagesStayNull()
    {
        var report = Build(Extraction(), success: false, error: "chunk activity failed");

        Assert.AreEqual(1, report.Extraction!.DocsToProcess);
        Assert.IsNull(report.Chunking);
        Assert.IsNull(report.Embedding);
        CollectionAssert.Contains(report.Extraction.RedFlags.ToList(), "extraction flag");
    }

    // Red flags stay on the stage that raised them instead of being merged into one list,
    // so a reader can tell an extraction flag from an upload flag without parsing strings.
    [TestMethod]
    public void RedFlags_StayAttributedToTheirOwnStage()
    {
        var report = Build(Extraction(redFlags: ["extract flag"]), Chunking(), EmbedUpload(redFlags: ["upload flag"]));

        CollectionAssert.AreEqual(new[] { "extract flag" }, report.Extraction!.RedFlags.ToList());
        CollectionAssert.AreEqual(new[] { "upload flag" }, report.Embedding!.RedFlags.ToList());
    }

    [TestMethod]
    public void HeadlineAccessors_ReadThroughToStages_AndDefaultToZeroWhenAbsent()
    {
        var full  = Build(Extraction(), Chunking(), EmbedUpload());
        var empty = Build();

        Assert.AreEqual(1, full.DocsToProcess);
        Assert.AreEqual(20, full.ChunksProduced);
        Assert.AreEqual(20, full.DocsUploaded);

        Assert.AreEqual(0, empty.DocsToProcess);
        Assert.AreEqual(0, empty.ChunksProduced);
        Assert.AreEqual(0, empty.DocsUploaded);
    }
}
