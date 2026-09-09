using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Infrastructure.Clients.Blob;
using AgenticRagApp.Infrastructure.Clients.Search;
using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Common.Models;
using AgenticRagApp.Observability.Reports;
using AgenticRagApp.Indexing.CU.Services;

namespace RagApp.UnitTests.CsvExtraction;

[TestClass]
public class ExtractionServiceTests
{
    // One successfully extracted file, as ExtractionService.ExtractFileAsync would return it.
    // ExtractionService runs the real ExtractionOutputBuilder over these, so the
    // PdfExtractionOutput these tests assert against is the one production builds - there is no
    // output to inject any more.
    private static ExtractedFile OkFile(string blobName) => new(
        Ok:        true,
        BlobName:  blobName,
        Content:   "content",
        PageSpans: [new PageSpan(1, 0, "content".Length, null)],
        Structure: new PdfDocumentStructure([], [], [], [], [], [], []),
        Title:     "",
        Language:  null,
        Usage:     null,
        Error:     null,
        Warnings:  []);

    private static ExtractedFile FailedFile(string blobName) =>
        ExtractedFile.Failed(
            blobName,
            PipelineIssue.Error(PipelineStage.ParsePages, blobName, "kapot", reason: PdfOpenFailureReason.Unknown));

    // Fakes the "documents" container's listing - what ExtractionService's own
    // ListDocumentsInBlobAsync reads (via IBlobStore) to build the "source" side of the
    // pre-extraction diff.
    private static Mock<IBlobStore> MockBlobStore(params (string Name, DateTimeOffset LastModified)[] blobs)
    {
        var store = new Mock<IBlobStore>();
        var empty = (IReadOnlyDictionary<string, string>)new Dictionary<string, string>();
        store.Setup(s => s.ListBlobsAsync(It.IsAny<BlobContainerClient>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(blobs.Select(b => (b.Name, (DateTimeOffset?)b.LastModified, (long?)null, empty)).ToList());
        return store;
    }

    // The real service with only its per-file extraction substituted - the one seam left in the
    // stage, now that both the corpus loop and one file's download/analyze/map live in
    // ExtractionService itself. What a test can stand in for is one file's extraction, not a
    // whole run's output.
    //
    // It records what it was asked for, which is what most tests below actually assert: the
    // pre-extraction diff decides which blobs are submitted at all, and a blob that never reaches
    // this override is a paid call that was correctly avoided.
    private sealed class TestExtractionService(
        Func<string, ExtractedFile> extract,
        IIndexDiffService           diffService,
        BlobContainerClient         documentsContainer,
        BlobContainerClient         stateContainer,
        IBlobStore                  blobStore,
        ExtractionReporter          reporter,
        TimeSpan?                   corpusWallClockLimit)
        : ExtractionService(
            diffService, documentsContainer,
            // The analyzer is never resolved: ExtractFileAsync is overridden below, and it is the
            // only thing that touches it or the documents container.
            null!,
            stateContainer, blobStore, reporter,
            NullLogger<ExtractionService>.Instance, corpusWallClockLimit)
    {
        private readonly List<string> _submitted = [];

        public IReadOnlyList<string> Submitted
        {
            get { lock (_submitted) return [.. _submitted]; }
        }

        internal override Task<ExtractedFile> ExtractFileAsync(string blobName, CancellationToken ct)
        {
            lock (_submitted) _submitted.Add(blobName);
            return Task.FromResult(extract(blobName));
        }
    }

    // Mirrors the real IndexDocumentService.GetCurrentIndexedDocumentDatesAsync, which builds
    // this dictionary with an OrdinalIgnoreCase comparer - the case-insensitive SourceId
    // matching in ExtractionService relies on that, not on anything it configures itself.
    private static Mock<IIndexDocumentService> MockIndexService(Dictionary<string, DateTimeOffset> indexedDates)
    {
        var caseInsensitive = new Dictionary<string, DateTimeOffset>(indexedDates, StringComparer.OrdinalIgnoreCase);
        var mock = new Mock<IIndexDocumentService>();
        mock.Setup(m => m.GetCurrentlyIndexedDocsIdsNDatesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(caseInsensitive);
        return mock;
    }

    private static Mock<IRunReportWriter> MockReportWriter(bool isEnabled = true)
    {
        var writer = new Mock<IRunReportWriter>();
        writer.SetupGet(w => w.IsEnabled).Returns(isEnabled);
        writer.Setup(w => w.WriteReportAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return writer;
    }

    // Wires a REAL IndexDiffService (not a mock) from the same blob/index mocks these tests
    // already set up. The listing + comparison moved out of ExtractionService into that class;
    // driving it for real here keeps every test below asserting the same end-to-end diff
    // behaviour it asserted before the split, which is what makes the extraction a provable
    // no-op refactor rather than a rewrite with re-pointed assertions.
    // IndexDiffServiceTests covers the same logic directly, at the unit level.
    private static TestExtractionService BuildService(
        Mock<IBlobStore> blobStore, Func<string, ExtractedFile> extract,
        Mock<IIndexDocumentService> indexService, Mock<IRunReportWriter> reportWriter,
        TimeSpan? corpusWallClockLimit = null) =>
        new(extract,
            BuildDiffService(blobStore, indexService),
            // Documents container. Only ExtractFileAsync reads from it, and that is overridden.
            new Mock<BlobContainerClient>().Object,
            // Run-state container. The same IBlobStore mock backs it, and its
            // TryReadJsonWithETagAsync/SaveJsonWithETagAsync are unconfigured - Moq returns
            // default, i.e. "no previous state", and the save is a no-op. That is the right
            // shape for these tests: they assert diff and stats behaviour, and nothing reads
            // the run-state value today - it is written as a baseline for a magnitude check
            // that is not wired yet (see FlagEvaluator).
            new Mock<BlobContainerClient>().Object,
            blobStore.Object,
            // The real reporter over the mocked writer: the report assertions below are about
            // what the stage writes, and a mocked reporter would assert nothing about that.
            new ExtractionReporter(reportWriter.Object, NullLogger<ExtractionReporter>.Instance),
            corpusWallClockLimit);

    private static IndexDiffService BuildDiffService(
        Mock<IBlobStore> blobStore, Mock<IIndexDocumentService> indexService) =>
        new(new Mock<BlobContainerClient>().Object, blobStore.Object, indexService.Object, NullLogger<IndexDiffService>.Instance);

    [TestMethod]
    public async Task NewDocument_NotYetIndexed_IsCountedAsNewAndProcessed()
    {
        var blobStore    = MockBlobStore(("doc1.pdf", DateTimeOffset.Parse("2024-01-01")));
        var indexService = MockIndexService([]);
        var service      = BuildService(blobStore, OkFile, indexService, MockReportWriter(isEnabled: false));

        var (docs, stats) = await service.ExtractAsync(forceReindex: false);

        Assert.AreEqual(1, docs.Count);
        Assert.AreEqual("doc1.pdf", docs[0].SourceId);
        Assert.AreEqual("pdf", stats.Source);
        Assert.AreEqual(1, stats.DocsNew);
        Assert.AreEqual(0, stats.DocsUpdated);
        Assert.AreEqual(0, stats.DocsSkipped);
        Assert.AreEqual(0, stats.DocsDeleted);
        Assert.AreEqual(0, stats.StaleDocumentIds.Count);
    }

    [TestMethod]
    public async Task UnmodifiedDocument_AlreadyIndexed_IsSkipped()
    {
        // Indexed AFTER the doc's LastModified - nothing new to do. Because it's skipped in
        // the pre-extraction diff, ExtractDocumentsAsync is never even asked for it -
        // docs.Count stays 0, proving the paid extraction call was avoided.
        var blobStore    = MockBlobStore(("doc1.pdf", DateTimeOffset.Parse("2024-01-01")));
        var indexService = MockIndexService(new() { ["doc1.pdf"] = DateTimeOffset.Parse("2024-06-01") });
        var service      = BuildService(blobStore, OkFile, indexService, MockReportWriter(isEnabled: false));

        var (docs, stats) = await service.ExtractAsync(forceReindex: false);

        Assert.AreEqual(0, docs.Count);
        Assert.AreEqual(0, stats.DocsNew);
        Assert.AreEqual(0, stats.DocsUpdated);
        Assert.AreEqual(1, stats.DocsSkipped);
    }

    [TestMethod]
    public async Task ModifiedDocument_AlreadyIndexed_IsCountedAsUpdatedAndMarkedStale()
    {
        // Indexed BEFORE the doc's LastModified - stale, needs reprocessing.
        var blobStore    = MockBlobStore(("doc1.pdf", DateTimeOffset.Parse("2024-06-01")));
        var indexService = MockIndexService(new() { ["doc1.pdf"] = DateTimeOffset.Parse("2024-01-01") });
        var service      = BuildService(blobStore, OkFile, indexService, MockReportWriter(isEnabled: false));

        var (docs, stats) = await service.ExtractAsync(forceReindex: false);

        Assert.AreEqual(1, docs.Count);
        Assert.AreEqual(0, stats.DocsNew);
        Assert.AreEqual(1, stats.DocsUpdated);
        Assert.AreEqual(0, stats.DocsSkipped);
        CollectionAssert.Contains(stats.StaleDocumentIds.ToList(), "doc1.pdf");
    }

    [TestMethod]
    public async Task ReUploadedLaterTheSameDay_IsCountedAsUpdatedNotSkipped()
    {
        // Regression test for finding #6: a re-upload indexed at 09:00, then corrected and
        // re-uploaded at 14:00 the same calendar day, must be detected as newer - full blob-
        // timestamp precision, not a same-day comparison that reads them as unchanged.
        var blobStore    = MockBlobStore(("doc1.pdf", DateTimeOffset.Parse("2024-06-01T14:00:00Z")));
        var indexService = MockIndexService(new() { ["doc1.pdf"] = DateTimeOffset.Parse("2024-06-01T09:00:00Z") });
        var service      = BuildService(blobStore, OkFile, indexService, MockReportWriter(isEnabled: false));

        var (docs, stats) = await service.ExtractAsync(forceReindex: false);

        Assert.AreEqual(1, docs.Count);
        Assert.AreEqual(1, stats.DocsUpdated);
        Assert.AreEqual(0, stats.DocsSkipped);
        CollectionAssert.Contains(stats.StaleDocumentIds.ToList(), "doc1.pdf");
    }

    [TestMethod]
    public async Task ForceReindex_ReprocessesEvenAnUnmodifiedDocument()
    {
        var blobStore    = MockBlobStore(("doc1.pdf", DateTimeOffset.Parse("2024-01-01")));
        var indexService = MockIndexService(new() { ["doc1.pdf"] = DateTimeOffset.Parse("2024-06-01") }); // would normally skip
        var service      = BuildService(blobStore, OkFile, indexService, MockReportWriter(isEnabled: false));

        var (docs, stats) = await service.ExtractAsync(forceReindex: true);

        Assert.AreEqual(1, docs.Count);
        Assert.AreEqual(1, stats.DocsUpdated);
        Assert.AreEqual(0, stats.DocsSkipped);
    }

    [TestMethod]
    public async Task HighNewDocFraction_WithExistingIndex_AddsRedFlag()
    {
        // Index already has doc1.pdf, but 2 of 3 source docs (doc2/doc3) read as "new" -
        // exactly the shape a truncated GetCurrentIndexedDocumentDatesAsync read produces
        // (finding #4): an existing, non-empty index that most of the corpus still misses.
        var blobStore = MockBlobStore(
            ("doc1.pdf", DateTimeOffset.Parse("2024-01-01")),
            ("doc2.pdf", DateTimeOffset.Parse("2024-01-01")),
            ("doc3.pdf", DateTimeOffset.Parse("2024-01-01")));
        var indexService = MockIndexService(new() { ["doc1.pdf"] = DateTimeOffset.Parse("2024-06-01") });
        var service      = BuildService(blobStore, OkFile, indexService, MockReportWriter(isEnabled: false));

        var (_, stats) = await service.ExtractAsync(forceReindex: false);

        Assert.IsTrue(stats.RedFlags.Any(f => f.StartsWith("high_new_doc_fraction")));
    }

    [TestMethod]
    public async Task AllNewOnFirstEverRun_NoExistingIndex_DoesNotAddHighNewDocFractionRedFlag()
    {
        // Every document being new is expected on a brand-new index (indexedDates empty) -
        // not a symptom of a truncated read, so this must not flag.
        var blobStore = MockBlobStore(
            ("doc1.pdf", DateTimeOffset.Parse("2024-01-01")),
            ("doc2.pdf", DateTimeOffset.Parse("2024-01-01")));
        var indexService = MockIndexService([]);
        var service      = BuildService(blobStore, OkFile, indexService, MockReportWriter(isEnabled: false));

        var (_, stats) = await service.ExtractAsync(forceReindex: false);

        Assert.IsFalse(stats.RedFlags.Any(f => f.StartsWith("high_new_doc_fraction")));
    }

    [TestMethod]
    public async Task HighNewDocFraction_OnForceReindex_DoesNotAddRedFlag()
    {
        // force=true legitimately reprocesses everything - not a truncation symptom either.
        var blobStore = MockBlobStore(
            ("doc1.pdf", DateTimeOffset.Parse("2024-01-01")),
            ("doc2.pdf", DateTimeOffset.Parse("2024-01-01")));
        var indexService = MockIndexService(new() { ["doc1.pdf"] = DateTimeOffset.Parse("2024-06-01") });
        var service      = BuildService(blobStore, OkFile, indexService, MockReportWriter(isEnabled: false));

        var (_, stats) = await service.ExtractAsync(forceReindex: true);

        Assert.IsFalse(stats.RedFlags.Any(f => f.StartsWith("high_new_doc_fraction")));
    }

    [TestMethod]
    public async Task DocumentRemovedFromSource_IsCountedAsDeletedAndMarkedStale()
    {
        // doc2.pdf was previously indexed but no longer appears in the blob listing at all - withdrawn upstream.
        var blobStore    = MockBlobStore(("doc1.pdf", DateTimeOffset.Parse("2024-01-01")));
        var indexService = MockIndexService(new() { ["doc2.pdf"] = DateTimeOffset.UtcNow });
        var service      = BuildService(blobStore, OkFile, indexService, MockReportWriter(isEnabled: false));

        var (docs, stats) = await service.ExtractAsync(forceReindex: false);

        Assert.AreEqual(1, docs.Count);
        Assert.AreEqual("doc1.pdf", docs[0].SourceId);
        Assert.AreEqual(1, stats.DocsNew);
        Assert.AreEqual(1, stats.DocsDeleted);
        CollectionAssert.Contains(stats.StaleDocumentIds.ToList(), "doc2.pdf");
    }

    [TestMethod]
    public async Task SourceIdMatching_IsCaseInsensitive()
    {
        // Blob named "DOC1.PDF", indexed as "doc1.pdf" - same document, must not be treated as
        // both a new doc AND a removed one.
        var blobStore    = MockBlobStore(("DOC1.PDF", DateTimeOffset.Parse("2024-01-01")));
        var indexService = MockIndexService(new() { ["doc1.pdf"] = DateTimeOffset.Parse("2024-06-01") });
        var service      = BuildService(blobStore, OkFile, indexService, MockReportWriter(isEnabled: false));

        var (_, stats) = await service.ExtractAsync(forceReindex: false);

        Assert.AreEqual(0, stats.DocsNew);
        Assert.AreEqual(0, stats.DocsDeleted);
        Assert.AreEqual(1, stats.DocsSkipped);
    }

    [TestMethod]
    public async Task NonPdfBlob_IsIgnored()
    {
        // A non-.pdf blob in the same container (e.g. a stray upload) must never be treated
        // as a source document - it's filtered out before the diff ever sees it.
        var blobStore    = MockBlobStore(("notes.txt", DateTimeOffset.Parse("2024-01-01")));
        var indexService = MockIndexService([]);
        var service      = BuildService(blobStore, OkFile, indexService, MockReportWriter(isEnabled: false));

        var (docs, stats) = await service.ExtractAsync(forceReindex: false);

        Assert.AreEqual(0, docs.Count);
        Assert.AreEqual(0, stats.DocsNew);
    }

    [TestMethod]
    public async Task Stats_ReportWhatTheRunActuallyProduced()
    {
        // There is no PdfExtractionOutput to inject any more - the loop is inside
        // ExtractionService and it builds the output through the real ExtractionOutputBuilder.
        // So this drives it the way production does: one file extracts, one fails, and the stats
        // row has to reflect exactly that.
        var blobStore = MockBlobStore(
            ("doc1.pdf", DateTimeOffset.Parse("2024-01-01")),
            ("doc2.pdf", DateTimeOffset.Parse("2024-01-01")));
        var indexService = MockIndexService([]);
        var service      = BuildService(
            blobStore,
            name => name == "doc2.pdf" ? FailedFile(name) : OkFile(name),
            indexService, MockReportWriter(isEnabled: false));

        var (docs, stats) = await service.ExtractAsync(forceReindex: false);

        // Only the successful file becomes a document; the failed one becomes an issue.
        Assert.AreEqual(1, docs.Count);
        Assert.AreEqual("doc1.pdf", docs[0].SourceId);
        Assert.AreEqual(1, stats.ValidationErrors);
        Assert.AreEqual(0, stats.ValidationWarnings);
        Assert.AreEqual(1, stats.Issues.Count);

        // Derived from the extracted document itself: no title, no headings.
        Assert.AreEqual(1, stats.MissingTitleCount);
        Assert.AreEqual(1, stats.DocsWithoutHeadings);

        // Null = "this source has no such concept", and must stay distinguishable from zero.
        Assert.IsNull(stats.StaleDocCount);
        Assert.IsNull(stats.MissingDepartmentCount);
        Assert.IsNull(stats.TraceabilityGapCount);
        Assert.IsNull(stats.MissingVersionCount);
    }

    [TestMethod]
    public async Task SkippedDocument_IsNeverSubmittedToTheFileExtractor()
    {
        // The point of the pre-extraction diff, asserted directly: an unchanged document must
        // not reach the per-file extraction at all, because reaching it means paying for it.
        var blobStore    = MockBlobStore(
            ("oud.pdf",  DateTimeOffset.Parse("2024-01-01")),
            ("nieuw.pdf", DateTimeOffset.Parse("2024-01-01")));
        var indexService = MockIndexService(new() { ["oud.pdf"] = DateTimeOffset.Parse("2024-06-01") });
        var service      = BuildService(blobStore, OkFile, indexService, MockReportWriter(isEnabled: false));

        await service.ExtractAsync(forceReindex: false);

        CollectionAssert.AreEqual(new[] { "nieuw.pdf" }, service.Submitted.ToList());
    }

    [TestMethod]
    public async Task ReportWriterEnabled_WritesDiffReportBlob()
    {
        var blobStore    = MockBlobStore(("doc1.pdf", DateTimeOffset.Parse("2024-01-01")));
        var indexService = MockIndexService([]);
        var reportWriter = MockReportWriter(isEnabled: true);
        var service      = BuildService(blobStore, OkFile, indexService, reportWriter);

        await service.ExtractAsync(forceReindex: false);

        reportWriter.Verify(w => w.WriteReportAsync(
            It.Is<string>(p => p.Contains("diff")), It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task ReportWriterDisabled_NoDiffReportWritten()
    {
        var blobStore    = MockBlobStore(("doc1.pdf", DateTimeOffset.Parse("2024-01-01")));
        var indexService = MockIndexService([]);
        var reportWriter = MockReportWriter(isEnabled: false);
        var service      = BuildService(blobStore, OkFile, indexService, reportWriter);

        await service.ExtractAsync(forceReindex: false);

        reportWriter.Verify(w => w.WriteReportAsync(
            It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task UpdatedDocument_WhoseExtractionFailed_IsNotMarkedStale()
    {
        // The case the stale filter exists for. doc1.pdf is already indexed and has been
        // modified, so the diff stages it for both re-extraction AND chunk deletion. Its
        // extraction then fails, so this run produced nothing to replace those chunks with.
        // Marking it stale anyway would have UploadService delete every chunk it has and write
        // none back - the document silently disappears from the index until someone touches the
        // blob again. Leaving it out means the old chunks stay served, slightly out of date,
        // and the next run retries it (the diff never advanced its indexed date).
        var blobStore    = MockBlobStore(("doc1.pdf", DateTimeOffset.Parse("2024-06-01")));
        var indexService = MockIndexService(new() { ["doc1.pdf"] = DateTimeOffset.Parse("2024-01-01") });
        var service      = BuildService(blobStore, FailedFile, indexService, MockReportWriter(isEnabled: false));

        var (docs, stats) = await service.ExtractAsync(forceReindex: false);

        // It was submitted and it did fail - this is the failure path, not a skip.
        CollectionAssert.AreEqual(new[] { "doc1.pdf" }, service.Submitted.ToList());
        Assert.AreEqual(0, docs.Count);
        Assert.AreEqual(1, stats.DocsUpdated);
        Assert.AreEqual(1, stats.ValidationErrors);

        CollectionAssert.DoesNotContain(stats.StaleDocumentIds.ToList(), "doc1.pdf");
    }

    [TestMethod]
    public async Task RemovedDocument_IsStillMarkedStale_EvenWhenAnotherExtractionFails()
    {
        // The other half of the same filter: it must not over-reach. doc2.pdf is gone from the
        // source, so there is no replacement content to wait for and never will be - its chunks
        // have to go regardless of what happened to doc1.pdf's extraction. A filter written as
        // "only mark stale what this run extracted" would wrongly strip it too, and the index
        // would keep serving a withdrawn document forever.
        var blobStore    = MockBlobStore(("doc1.pdf", DateTimeOffset.Parse("2024-06-01")));
        var indexService = MockIndexService(new()
        {
            ["doc1.pdf"] = DateTimeOffset.Parse("2024-01-01"), // indexed + modified -> updated
            ["doc2.pdf"] = DateTimeOffset.Parse("2024-01-01"), // indexed, absent from listing -> removed
        });
        var service      = BuildService(blobStore, FailedFile, indexService, MockReportWriter(isEnabled: false));

        var (_, stats) = await service.ExtractAsync(forceReindex: false);

        Assert.AreEqual(1, stats.DocsDeleted);
        CollectionAssert.Contains(stats.StaleDocumentIds.ToList(), "doc2.pdf");
        CollectionAssert.DoesNotContain(stats.StaleDocumentIds.ToList(), "doc1.pdf");
    }

    [TestMethod]
    public async Task CorpusWallClockLimitReached_DocumentIsNeitherSubmittedNorMarkedStale()
    {
        // The likelier way an updated document ends a run with no replacement content: the run
        // stopped submitting new files before reaching it. Same conclusion as a failed
        // extraction - nothing to swap in, so nothing may be deleted - but it arrives without any
        // error being recorded, which is exactly why the filter keys on "did this run produce a
        // document for it" rather than on the error list.
        //
        // A negative limit rather than TimeSpan.Zero: the elapsed check is `> limit`, and with an
        // all-mocked diff the clock can still read zero ticks by the time the loop starts.
        var blobStore    = MockBlobStore(("doc1.pdf", DateTimeOffset.Parse("2024-06-01")));
        var indexService = MockIndexService(new() { ["doc1.pdf"] = DateTimeOffset.Parse("2024-01-01") });
        var service      = BuildService(
            blobStore, OkFile, indexService, MockReportWriter(isEnabled: false),
            corpusWallClockLimit: TimeSpan.FromTicks(-1));

        var (docs, stats) = await service.ExtractAsync(forceReindex: false);

        Assert.AreEqual(0, service.Submitted.Count);
        Assert.AreEqual(0, docs.Count);
        // Not an error, and still counted as updated by the diff - it simply did not run.
        Assert.AreEqual(1, stats.DocsUpdated);
        Assert.AreEqual(0, stats.ValidationErrors);

        CollectionAssert.DoesNotContain(stats.StaleDocumentIds.ToList(), "doc1.pdf");
    }
}
