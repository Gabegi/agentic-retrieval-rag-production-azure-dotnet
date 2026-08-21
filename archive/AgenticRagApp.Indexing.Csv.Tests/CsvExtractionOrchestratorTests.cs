using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using AgenticRagApp.Infrastructure.Clients.Blob;
using AgenticRagApp.Indexing.Csv.Services;
using AgenticRagApp.Observability.Reports;

namespace RagApp.UnitTests.CsvExtraction;

[TestClass]
public class CsvExtractionOrchestratorTests
{
    private const string PagesBlobName = "zenya_pages.csv";
    private const string IndexBlobName = "zenya_index.csv";
    private const string StateBlobName = "csv-extraction-state.json";

    private const string PagesHeader =
        "DOCUMENT_ID,TITLE,QUICK_CODE,FOLDER_MINI_FULL_PATH,LAST_MODIFIED_DATETIME,PAGE_INDEX,PAGE_CONTENT,RELATIVE_PATH,LANGUAGE";
    private const string IndexHeader =
        "DOCUMENT_ID,DOCUMENT_TYPE_NAME,SUMMARY,VERSION,REVISION,CHECK_DATE,ATTENTION_REQUIRED_FLAGS,ACTIVE";

    // One matched, active document/page - a clean run with nothing to complain about.
    private const string OnePageCsv  = PagesHeader + "\n" + "doc1,Title,QC,Folder,20240101120000,0,# Heading\\nSome content,rel,nl-NL\n";
    private const string OneIndexCsv = IndexHeader + "\n" + "doc1,Protocol,Summary,7,0,,[],true\n";

    // Same document, but marked inactive - joins to zero pages, so cleanResult ends up
    // empty even though every row parses fine (exercises the zero-cleaned-records guard).
    private const string InactiveIndexCsv = IndexHeader + "\n" + "doc1,Protocol,Summary,7,0,,[],false\n";

    private static Stream ToStream(string csv) => new MemoryStream(Encoding.UTF8.GetBytes(csv));

    // Mocks IBlobStore's stream reads for the two source blobs (pages.csv/index.csv) and
    // the ETag-based read/write of the run-state blob - the orchestrator never touches
    // BlobContainerClient/BlobClient directly anymore, everything funnels through this.
    private static Mock<IBlobStore> MockBlobStore(
        string pagesCsv, string indexCsv,
        CsvExtractionOrchestrator.RunState? previousState = null, ETag? previousETag = null)
    {
        var store = new Mock<IBlobStore>();

        store.Setup(s => s.OpenReadAsync(It.IsAny<BlobContainerClient>(), PagesBlobName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ToStream(pagesCsv));
        store.Setup(s => s.OpenReadAsync(It.IsAny<BlobContainerClient>(), IndexBlobName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ToStream(indexCsv));

        store.Setup(s => s.TryReadJsonWithETagAsync<CsvExtractionOrchestrator.RunState>(
                It.IsAny<BlobContainerClient>(), StateBlobName, It.IsAny<CancellationToken>()))
            .ReturnsAsync((previousState, previousETag));

        store.Setup(s => s.SaveJsonWithETagAsync(
                It.IsAny<BlobContainerClient>(), StateBlobName, It.IsAny<CsvExtractionOrchestrator.RunState>(), It.IsAny<ETag?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        return store;
    }

    private static Mock<IRunReportWriter> MockReportWriter(bool isEnabled = true)
    {
        var writer = new Mock<IRunReportWriter>();
        writer.SetupGet(w => w.IsEnabled).Returns(isEnabled);
        writer.Setup(w => w.WriteReportAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return writer;
    }

    private static CsvExtractionOrchestrator BuildOrchestrator(
        Mock<IBlobStore> blobStore, IRunReportWriter reportWriter) =>
        new(
            new Mock<BlobContainerClient>().Object, new Mock<BlobContainerClient>().Object, blobStore.Object, reportWriter,
            new CsvExtractor(NullLogger<CsvExtractor>.Instance),
            new CsvJoiner(),
            new DataCleaner(),
            new PipelineValidator(),
            NullLogger<CsvExtractionOrchestrator>.Instance);

    [TestMethod]
    public async Task HappyPath_ReturnsDocsAndSavesStateUnconditionally()
    {
        var blobStore    = MockBlobStore(OnePageCsv, OneIndexCsv); // first-ever run, no previous state
        var reportWriter = MockReportWriter();
        var orchestrator = BuildOrchestrator(blobStore, reportWriter.Object);

        var output = await orchestrator.ExtractDocumentsAsync();

        Assert.AreEqual(1, output.Docs.Count);
        Assert.AreEqual("doc1", output.Docs[0].SourceId);
        Assert.AreEqual(0, output.ValidationErrors);

        blobStore.Verify(s => s.SaveJsonWithETagAsync(
            It.IsAny<BlobContainerClient>(), StateBlobName,
            It.Is<CsvExtractionOrchestrator.RunState>(r => r.CleanedRecords == 1), null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task ZeroCleanedRecords_ThrowsAndNeverSavesState()
    {
        // Every row parses, but the sole document is inactive -> zero cleaned records ->
        // the reconciliation hard-gate should fail the run before state is ever saved.
        var blobStore    = MockBlobStore(OnePageCsv, InactiveIndexCsv);
        var reportWriter = MockReportWriter();
        var orchestrator = BuildOrchestrator(blobStore, reportWriter.Object);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => orchestrator.ExtractDocumentsAsync());

        blobStore.Verify(s => s.SaveJsonWithETagAsync(
            It.IsAny<BlobContainerClient>(), StateBlobName, It.IsAny<CsvExtractionOrchestrator.RunState>(), It.IsAny<ETag?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task MagnitudeShiftWithoutOverride_Throws()
    {
        // Previous run had 100 cleaned records; this run only has 1 - a -99% shift, well
        // past the 20% threshold PipelineValidator enforces.
        var blobStore    = MockBlobStore(OnePageCsv, OneIndexCsv, new CsvExtractionOrchestrator.RunState(100));
        var reportWriter = MockReportWriter();
        var orchestrator = BuildOrchestrator(blobStore, reportWriter.Object);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => orchestrator.ExtractDocumentsAsync(overrideMagnitudeCheck: false));

        blobStore.Verify(s => s.SaveJsonWithETagAsync(
            It.IsAny<BlobContainerClient>(), StateBlobName, It.IsAny<CsvExtractionOrchestrator.RunState>(), It.IsAny<ETag?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task MagnitudeShiftWithOverride_ProceedsAndSavesStateConditionally()
    {
        var etag      = new ETag("\"baseline\"");
        var blobStore = MockBlobStore(OnePageCsv, OneIndexCsv, new CsvExtractionOrchestrator.RunState(100), etag);
        var reportWriter = MockReportWriter();
        var orchestrator = BuildOrchestrator(blobStore, reportWriter.Object);

        var output = await orchestrator.ExtractDocumentsAsync(overrideMagnitudeCheck: true);

        Assert.AreEqual(1, output.Docs.Count);

        // A conditional write against the baseline's own ETag, not an unconditional
        // "only if the blob doesn't exist yet" write - this run had a real baseline to race against.
        blobStore.Verify(s => s.SaveJsonWithETagAsync(
            It.IsAny<BlobContainerClient>(), StateBlobName, It.IsAny<CsvExtractionOrchestrator.RunState>(), etag, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task NoBaseline_RunStillSucceeds()
    {
        // Whether there's no baseline because the state blob never existed or because it
        // was corrupt is normalized to (null, null) by IBlobStore before the orchestrator
        // ever sees it (see BlobStoreTests for that specific behavior) - either way, the
        // orchestrator just treats it as a first run.
        var blobStore    = MockBlobStore(OnePageCsv, OneIndexCsv, previousState: null);
        var reportWriter = MockReportWriter();
        var orchestrator = BuildOrchestrator(blobStore, reportWriter.Object);

        var output = await orchestrator.ExtractDocumentsAsync();

        Assert.AreEqual(1, output.Docs.Count);
    }

    [TestMethod]
    public async Task PagesParseError_WritesSingleValidationReportBlob()
    {
        var badPagesCsv = PagesHeader + "\n" + "doc1,Title,QC,Folder,20240101120000,not-a-number,Content,rel,nl-NL\n";
        var blobStore    = MockBlobStore(badPagesCsv, OneIndexCsv);
        var reportWriter = MockReportWriter();
        var orchestrator = BuildOrchestrator(blobStore, reportWriter.Object);

        // The page fails to parse, and doc1's index record then has zero matching pages,
        // which also fails validation (RowsAttempted denominator makes this a 100% error rate) -
        // that's fine, this test only cares that the one consolidated report blob got written.
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => orchestrator.ExtractDocumentsAsync());

        reportWriter.Verify(w => w.WriteReportAsync(
            It.Is<string>(p => p.Contains("csv-validation")), It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task ReportWriterDisabled_NoBlobsWritten()
    {
        var badPagesCsv = PagesHeader + "\n" + "doc1,Title,QC,Folder,20240101120000,not-a-number,Content,rel,nl-NL\n";
        var blobStore    = MockBlobStore(badPagesCsv, OneIndexCsv);
        var reportWriter = MockReportWriter(isEnabled: false);
        var orchestrator = BuildOrchestrator(blobStore, reportWriter.Object);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => orchestrator.ExtractDocumentsAsync());

        reportWriter.Verify(w => w.WriteReportAsync(
            It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
