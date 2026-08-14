using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Infrastructure.Clients.Blob;
using AgenticRagApp.Infrastructure.Clients.Search;
using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Observability.Reports;
using AgenticRagApp.Indexing.Pdf.Services;

namespace RagApp.UnitTests.CsvExtraction;

[TestClass]
public class ExtractionServiceTests
{
    private static PdfExtractionDocument Doc(string sourceId) => new(
        SourceId:              sourceId,
        Content:               "content",
        Title:                 "",
        Author:                null,
        CreatedAt:             null,
        ModDate:               null,
        PageCount:             null,
        LastModifiedDate:      null,
        ZenyaDocumentId:       null,
        ZenyaVersion:          null,
        ZenyaStatus:           null,
        ZenyaUrl:              null,
        Bookmarks:             [],
        PageSpans:        [new PageSpan(1, 0, "content".Length, null, false)],
        PageBreadcrumbs:  new Dictionary<int, string>(),
        Sections:              [],
        Headings:              [],
        Boilerplate:           [],
        Tables:                [],
        SelectionMarks:        [],
        Figures:               [],
        Lines:            [],
        Profile:          null,
        Language:         null);

    private static PdfExtractionOutput BuildOutput(IEnumerable<PdfExtractionDocument> docs) => new(docs.ToList())
    {
        ValidationErrors       = 0,
        ValidationWarnings     = 0,
        ReconciliationProblems = 0,
        StaleDocCount          = 0,
        MojibakeRepairedPages  = 0,
        DetectedTableCount     = 0,
        DocsWithoutHeadings    = 0,
        MissingTitleCount      = 0,
        MissingVersionCount    = 0,
        MissingDepartmentCount = 0,
        TraceabilityGapCount   = 0,
        Issues                 = [],
        RedFlags               = [],
        SpotCheckSample        = [],
    };

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

    // Variant that lets a test attach blob metadata (e.g. zenya_status) per blob - used for
    // the Zenya-inactive exclusion tests.
    private static Mock<IBlobStore> MockBlobStoreWithMetadata(
        params (string Name, DateTimeOffset LastModified, IReadOnlyDictionary<string, string> Metadata)[] blobs)
    {
        var store = new Mock<IBlobStore>();
        store.Setup(s => s.ListBlobsAsync(It.IsAny<BlobContainerClient>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(blobs.Select(b => (b.Name, (DateTimeOffset?)b.LastModified, (long?)null, b.Metadata)).ToList());
        return store;
    }

    // ExtractDocumentsAsync is stubbed to mirror what the real PdfExtractionOrchestrator does -
    // it only returns ExtractionDocuments for whichever ids ExtractionService actually asked it
    // to process, so tests exercise the same pre-extraction-diff contract the real pipeline relies on.
    private static Mock<IExtractionOrchestrator> MockExtractor(string source = "pdf")
    {
        var mock = new Mock<IExtractionOrchestrator>();
        mock.SetupGet(m => m.Source).Returns(source);
        mock.Setup(m => m.ExtractDocumentsAsync(It.IsAny<IReadOnlyDictionary<string, PdfBlobInfo>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<string, PdfBlobInfo> sourceIdsToProcess, string? _, CancellationToken __) => BuildOutput(sourceIdsToProcess.Keys.Select(Doc)));
        return mock;
    }

    // Variant for tests that need a fixed PdfExtractionOutput (e.g. asserting validation
    // fields propagate) rather than one derived from whatever ids got requested.
    private static Mock<IExtractionOrchestrator> MockExtractorWithFixedOutput(PdfExtractionOutput output, string source = "pdf")
    {
        var mock = new Mock<IExtractionOrchestrator>();
        mock.SetupGet(m => m.Source).Returns(source);
        mock.Setup(m => m.ExtractDocumentsAsync(It.IsAny<IReadOnlyDictionary<string, PdfBlobInfo>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(output);
        return mock;
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

    private static ExtractionService BuildService(
        Mock<IBlobStore> blobStore, Mock<IExtractionOrchestrator> extractor,
        Mock<IIndexDocumentService> indexService, Mock<IRunReportWriter> reportWriter) =>
        new(new Mock<BlobContainerClient>().Object, blobStore.Object, extractor.Object, indexService.Object, reportWriter.Object, NullLogger<ExtractionService>.Instance);

    [TestMethod]
    public async Task NewDocument_NotYetIndexed_IsCountedAsNewAndProcessed()
    {
        var blobStore    = MockBlobStore(("doc1.pdf", DateTimeOffset.Parse("2024-01-01")));
        var extractor    = MockExtractor();
        var indexService = MockIndexService([]);
        var service      = BuildService(blobStore, extractor, indexService, MockReportWriter(isEnabled: false));

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
        var extractor    = MockExtractor();
        var indexService = MockIndexService(new() { ["doc1.pdf"] = DateTimeOffset.Parse("2024-06-01") });
        var service      = BuildService(blobStore, extractor, indexService, MockReportWriter(isEnabled: false));

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
        var extractor    = MockExtractor();
        var indexService = MockIndexService(new() { ["doc1.pdf"] = DateTimeOffset.Parse("2024-01-01") });
        var service      = BuildService(blobStore, extractor, indexService, MockReportWriter(isEnabled: false));

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
        var extractor    = MockExtractor();
        var indexService = MockIndexService(new() { ["doc1.pdf"] = DateTimeOffset.Parse("2024-06-01T09:00:00Z") });
        var service      = BuildService(blobStore, extractor, indexService, MockReportWriter(isEnabled: false));

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
        var extractor    = MockExtractor();
        var indexService = MockIndexService(new() { ["doc1.pdf"] = DateTimeOffset.Parse("2024-06-01") }); // would normally skip
        var service      = BuildService(blobStore, extractor, indexService, MockReportWriter(isEnabled: false));

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
        var extractor    = MockExtractor();
        var indexService = MockIndexService(new() { ["doc1.pdf"] = DateTimeOffset.Parse("2024-06-01") });
        var service      = BuildService(blobStore, extractor, indexService, MockReportWriter(isEnabled: false));

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
        var extractor    = MockExtractor();
        var indexService = MockIndexService([]);
        var service      = BuildService(blobStore, extractor, indexService, MockReportWriter(isEnabled: false));

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
        var extractor    = MockExtractor();
        var indexService = MockIndexService(new() { ["doc1.pdf"] = DateTimeOffset.Parse("2024-06-01") });
        var service      = BuildService(blobStore, extractor, indexService, MockReportWriter(isEnabled: false));

        var (_, stats) = await service.ExtractAsync(forceReindex: true);

        Assert.IsFalse(stats.RedFlags.Any(f => f.StartsWith("high_new_doc_fraction")));
    }

    [TestMethod]
    public async Task DocumentRemovedFromSource_IsCountedAsDeletedAndMarkedStale()
    {
        // doc2.pdf was previously indexed but no longer appears in the blob listing at all - withdrawn upstream.
        var blobStore    = MockBlobStore(("doc1.pdf", DateTimeOffset.Parse("2024-01-01")));
        var extractor    = MockExtractor();
        var indexService = MockIndexService(new() { ["doc2.pdf"] = DateTimeOffset.UtcNow });
        var service      = BuildService(blobStore, extractor, indexService, MockReportWriter(isEnabled: false));

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
        var extractor    = MockExtractor();
        var indexService = MockIndexService(new() { ["doc1.pdf"] = DateTimeOffset.Parse("2024-06-01") });
        var service      = BuildService(blobStore, extractor, indexService, MockReportWriter(isEnabled: false));

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
        var extractor    = MockExtractor();
        var indexService = MockIndexService([]);
        var service      = BuildService(blobStore, extractor, indexService, MockReportWriter(isEnabled: false));

        var (docs, stats) = await service.ExtractAsync(forceReindex: false);

        Assert.AreEqual(0, docs.Count);
        Assert.AreEqual(0, stats.DocsNew);
    }

    [TestMethod]
    public async Task ZenyaInactiveNewDocument_IsNeverProcessed()
    {
        // A new blob whose zenya_status metadata is already "ingetrokken" (withdrawn) must
        // never be processed - Zenya said it's invalid before it was ever indexed.
        var metadata  = new Dictionary<string, string> { ["zenya_status"] = "ingetrokken" };
        var blobStore = MockBlobStoreWithMetadata(("doc1.pdf", DateTimeOffset.Parse("2024-01-01"), metadata));
        var extractor = MockExtractor();
        var indexService = MockIndexService([]);
        var service   = BuildService(blobStore, extractor, indexService, MockReportWriter(isEnabled: false));

        var (docs, stats) = await service.ExtractAsync(forceReindex: false);

        Assert.AreEqual(0, docs.Count);
        Assert.AreEqual(0, stats.DocsNew);
        Assert.AreEqual(0, stats.DocsDeleted);
    }

    [TestMethod]
    public async Task ZenyaInactiveIndexedDocument_IsTornDownLikeRemoved()
    {
        // Currently indexed, still present as a blob, but Zenya now marks it withdrawn -
        // must be deleted from the index even though the blob itself is still there.
        var metadata  = new Dictionary<string, string> { ["zenya_status"] = "vervangen" };
        var blobStore = MockBlobStoreWithMetadata(("doc1.pdf", DateTimeOffset.Parse("2024-06-01"), metadata));
        var extractor = MockExtractor();
        var indexService = MockIndexService(new() { ["doc1.pdf"] = DateTimeOffset.Parse("2024-01-01") });
        var service   = BuildService(blobStore, extractor, indexService, MockReportWriter(isEnabled: false));

        var (docs, stats) = await service.ExtractAsync(forceReindex: false);

        Assert.AreEqual(0, docs.Count);
        Assert.AreEqual(1, stats.DocsDeleted);
        CollectionAssert.Contains(stats.StaleDocumentIds.ToList(), "doc1.pdf");
    }

    [TestMethod]
    public async Task NoZenyaMetadataSet_IsTreatedAsActive()
    {
        // Fail-open: a blob with no zenya_status at all (today's default, since uploads are
        // manual) must still be indexed normally, not excluded.
        var blobStore = MockBlobStore(("doc1.pdf", DateTimeOffset.Parse("2024-01-01")));
        var extractor = MockExtractor();
        var indexService = MockIndexService([]);
        var service   = BuildService(blobStore, extractor, indexService, MockReportWriter(isEnabled: false));

        var (docs, stats) = await service.ExtractAsync(forceReindex: false);

        Assert.AreEqual(1, docs.Count);
        Assert.AreEqual(1, stats.DocsNew);
    }

    [TestMethod]
    public async Task Stats_PropagatesValidationFieldsFromExtractionOutput()
    {
        var output = new PdfExtractionOutput([Doc("doc1.pdf")])
        {
            ValidationErrors       = 3,
            ValidationWarnings     = 5,
            ReconciliationProblems = 1,
            StaleDocCount          = 2,
            MojibakeRepairedPages  = 6,
            DetectedTableCount     = 7,
            DocsWithoutHeadings    = 4,
            MissingTitleCount      = 1,
            MissingVersionCount    = 2,
            MissingDepartmentCount = 3,
            TraceabilityGapCount   = 9,
            Issues                 = [],
            RedFlags               = ["some flag"],
            SpotCheckSample        = [],
        };
        var blobStore    = MockBlobStore(("doc1.pdf", DateTimeOffset.Parse("2024-01-01")));
        var extractor    = MockExtractorWithFixedOutput(output);
        var indexService = MockIndexService([]);
        var service      = BuildService(blobStore, extractor, indexService, MockReportWriter(isEnabled: false));

        var (_, stats) = await service.ExtractAsync(forceReindex: false);

        Assert.AreEqual(3, stats.ValidationErrors);
        Assert.AreEqual(5, stats.ValidationWarnings);
        Assert.AreEqual(1, stats.ReconciliationProblems);
        Assert.AreEqual(2, stats.StaleDocCount);
        Assert.AreEqual(6, stats.MojibakeRepairedPages);
        Assert.AreEqual(7, stats.DetectedTableCount);
        Assert.AreEqual(4, stats.DocsWithoutHeadings);
        Assert.AreEqual(1, stats.MissingTitleCount);
        Assert.AreEqual(2, stats.MissingVersionCount);
        Assert.AreEqual(3, stats.MissingDepartmentCount);
        Assert.AreEqual(9, stats.TraceabilityGapCount);
        CollectionAssert.Contains(stats.RedFlags.ToList(), "some flag");
    }

    [TestMethod]
    public async Task ReportWriterEnabled_WritesDiffReportBlob()
    {
        var blobStore    = MockBlobStore(("doc1.pdf", DateTimeOffset.Parse("2024-01-01")));
        var extractor    = MockExtractor();
        var indexService = MockIndexService([]);
        var reportWriter = MockReportWriter(isEnabled: true);
        var service      = BuildService(blobStore, extractor, indexService, reportWriter);

        await service.ExtractAsync(forceReindex: false);

        reportWriter.Verify(w => w.WriteReportAsync(
            It.Is<string>(p => p.Contains("diff")), It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task ReportWriterDisabled_NoDiffReportWritten()
    {
        var blobStore    = MockBlobStore(("doc1.pdf", DateTimeOffset.Parse("2024-01-01")));
        var extractor    = MockExtractor();
        var indexService = MockIndexService([]);
        var reportWriter = MockReportWriter(isEnabled: false);
        var service      = BuildService(blobStore, extractor, indexService, reportWriter);

        await service.ExtractAsync(forceReindex: false);

        reportWriter.Verify(w => w.WriteReportAsync(
            It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
