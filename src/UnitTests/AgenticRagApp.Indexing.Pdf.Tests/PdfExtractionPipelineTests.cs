using System.Linq;
using Azure;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using AgenticRagApp.Infrastructure.Clients.Blob;
using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Indexing.Pdf.Services;
using AgenticRagApp.Observability.Reports;
using AgenticRagApp.Common.Models;

namespace RagApp.UnitTests.PdfExtraction;

[TestClass]
public class PdfExtractionPipelineTests
{
    private const string StateBlobName = "pdf-extraction-state.json";

    // sourceIdsToProcess entries the pipeline now receives directly (see ExtractionService's
    // own pre-extraction blob listing/diff) - no need for the pipeline to list the container
    // itself, so tests only build the entries it's asked to process, same shape ExtractionService
    // hands over for real.
    private static Dictionary<string, PdfBlobInfo> Entries(params string[] names) =>
        names.ToDictionary(n => n, _ => new PdfBlobInfo(DateTimeOffset.UtcNow, 100, ZenyaMetadata.Empty), StringComparer.OrdinalIgnoreCase);

    private static PdfExtractionResult SuccessResult(string blobName) => new(
        Ok: true, BlobName: blobName, FileSizeBytes: 100, PdfSpecVersion: 1.7,
        NativeMetadata: null, RawContent: "content",
        Pages: [new PdfPageRecord { BlobName = blobName, PageNumber = 1, PageContent = "content", Title = "Title" }],
        Structure: null, EstimatedCostUsd: 0.01m, Error: null);

    private static PdfCleanResult OneRecordCleanResult(string blobName)
    {
        var result = new PdfCleanResult();
        result.AddRecord(new CleanedPdfPageRecord { BlobName = blobName, PageNumber = 1, PageContent = "content", Title = "Title" });
        return result;
    }

    private static Mock<IBlobStore> MockBlobStore(byte[]? pdfBytes = null)
    {
        var store = new Mock<IBlobStore>();

        store.Setup(s => s.DownloadBytesAsync(It.IsAny<BlobContainerClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(pdfBytes ?? [1, 2, 3]);

        // PdfExtractionPipeline.RunState is a private nested record, so it can't be named
        // here - Moq's automatic Task<T> handling for unconfigured members returns
        // Task.FromResult(default(T)) for these generic calls, which is exactly the
        // "no previous baseline" / "save succeeded" shape the pipeline already tolerates.

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

    private static Mock<IHostEnvironment> MockEnvironmentImpl(string environmentName)
    {
        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns(environmentName);
        return env;
    }

    private static PdfExtractionPipeline BuildPipeline(
        Mock<IBlobStore> blobStore, Mock<IRunReportWriter> reportWriter,
        Mock<IPdfExtractor> extractor, Mock<IPdfCleaner> cleaner, Mock<IPdfPipelineValidator> validator,
        Mock<IHostEnvironment> env, TimeSpan? corpusWallClockLimit = null,
        ILogger<PdfExtractionPipeline>? logger = null) =>
        new(
            new Mock<BlobContainerClient>().Object, new Mock<BlobContainerClient>().Object,
            blobStore.Object, reportWriter.Object, extractor.Object, cleaner.Object, validator.Object,
            env.Object, logger ?? NullLogger<PdfExtractionPipeline>.Instance, corpusWallClockLimit);

    // Captures formatted log messages so tests can assert on what a warning actually said.
    // Needed because validation no longer aborts the run - the content of the warning is the
    // only remaining observable for "what tripped the quality gate".
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private static Mock<IPdfExtractor> MockExtractor(params string[] blobNames)
    {
        var extractor = new Mock<IPdfExtractor>();
        foreach (var name in blobNames)
            extractor.Setup(e => e.ExtractPDFAsync(name, It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(SuccessResult(name));
        return extractor;
    }

    [TestMethod]
    public async Task HappyPath_ReturnsDocs()
    {
        var blobStore    = MockBlobStore();
        var reportWriter = MockReportWriter();
        var extractor    = MockExtractor("doc1.pdf");
        var cleaner      = new Mock<IPdfCleaner>();
        cleaner.Setup(c => c.CleanPdf(It.IsAny<IReadOnlyList<PdfPageRecord>>())).Returns(OneRecordCleanResult("doc1.pdf"));
        var validator = new Mock<IPdfPipelineValidator>();
        validator.Setup(v => v.Validate(It.IsAny<IReadOnlyList<PdfExtractionResult>>(), It.IsAny<PdfCleanResult>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .Returns(new PdfQualityGateResult { Passed = true, CleanedRecords = 1 });
        var env = MockEnvironmentImpl("Production");

        var pipeline = BuildPipeline(blobStore, reportWriter, extractor, cleaner, validator, env);

        var output = await pipeline.ExtractDocumentsAsync(Entries("doc1.pdf"));

        Assert.AreEqual(1, output.Docs.Count);
        Assert.AreEqual("doc1.pdf", output.Docs[0].SourceId);
    }

    [TestMethod]
    public async Task MultipleFiles_CleanedPerFileAndMerged_AllRecordsReachOutput()
    {
        // Regression test for finding #14: cleaning now runs per-file inside the parallel
        // extraction loop (PdfCleaner.CleanPdf called once per successfully-extracted file,
        // merged via PdfCleanResult.MergeFrom) instead of once for the whole flattened
        // corpus. Every other test in this file uses a single blob, where per-file vs.
        // whole-batch cleaning is indistinguishable - this asserts both the per-file call
        // count and that the merge doesn't drop or duplicate any file's records.
        var blobStore    = MockBlobStore();
        var reportWriter = MockReportWriter();
        var extractor    = MockExtractor("doc1.pdf", "doc2.pdf", "doc3.pdf");
        var cleaner      = new Mock<IPdfCleaner>();
        cleaner.Setup(c => c.CleanPdf(It.Is<IReadOnlyList<PdfPageRecord>>(p => p.Count == 1 && p[0].BlobName == "doc1.pdf")))
            .Returns(OneRecordCleanResult("doc1.pdf"));
        cleaner.Setup(c => c.CleanPdf(It.Is<IReadOnlyList<PdfPageRecord>>(p => p.Count == 1 && p[0].BlobName == "doc2.pdf")))
            .Returns(OneRecordCleanResult("doc2.pdf"));
        cleaner.Setup(c => c.CleanPdf(It.Is<IReadOnlyList<PdfPageRecord>>(p => p.Count == 1 && p[0].BlobName == "doc3.pdf")))
            .Returns(OneRecordCleanResult("doc3.pdf"));
        var validator = new Mock<IPdfPipelineValidator>();
        PdfCleanResult? capturedCleanResult = null;
        validator.Setup(v => v.Validate(It.IsAny<IReadOnlyList<PdfExtractionResult>>(), It.IsAny<PdfCleanResult>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .Callback<IReadOnlyList<PdfExtractionResult>, PdfCleanResult, int?, int?>((_, clean, _, _) => capturedCleanResult = clean)
            .Returns(new PdfQualityGateResult { Passed = true, CleanedRecords = 3 });
        var env = MockEnvironmentImpl("Production");

        var pipeline = BuildPipeline(blobStore, reportWriter, extractor, cleaner, validator, env);

        var output = await pipeline.ExtractDocumentsAsync(Entries("doc1.pdf", "doc2.pdf", "doc3.pdf"));

        // CleanPdf called once per file - never once with all three files' pages flattened
        // together (the old whole-corpus-at-once shape this finding replaced).
        cleaner.Verify(c => c.CleanPdf(It.Is<IReadOnlyList<PdfPageRecord>>(p => p.Count == 1)), Times.Exactly(3));
        cleaner.Verify(c => c.CleanPdf(It.Is<IReadOnlyList<PdfPageRecord>>(p => p.Count > 1)), Times.Never);

        Assert.IsNotNull(capturedCleanResult);
        Assert.AreEqual(3, capturedCleanResult!.Records.Count);
        CollectionAssert.AreEquivalent(
            new[] { "doc1.pdf", "doc2.pdf", "doc3.pdf" },
            capturedCleanResult.Records.Select(r => r.BlobName).ToList());

        Assert.AreEqual(3, output.Docs.Count);
    }

    [TestMethod]
    public async Task IssuesOverReturnCap_ErrorSeverityIssueStillReachesOutput()
    {
        // Regression test for finding #9's other half: PdfPipelineValidator.Validate
        // already sorts errors ahead of warnings (see PdfPipelineValidatorTests'
        // ErrorSeverityIssues_AreSortedAheadOfWarnings test), but BuildExtractionOutput
        // separately caps report.Issues to MaxReturnedIssues (100) for Durable's row-size
        // limit. This proves the two combine correctly at real scale: with 150 warnings
        // ahead of the one error in assembly order (matching a real run's Metadata-then-
        // TextQuality stage order), the error must still be in output.Issues after the cap
        // - not just after Validate's own sort, which a unit test on Validate alone can't see.
        var blobStore    = MockBlobStore();
        var reportWriter = MockReportWriter();
        var extractor    = MockExtractor("doc1.pdf");
        var cleaner      = new Mock<IPdfCleaner>();
        cleaner.Setup(c => c.CleanPdf(It.IsAny<IReadOnlyList<PdfPageRecord>>())).Returns(OneRecordCleanResult("doc1.pdf"));

        var warnings = Enumerable.Range(1, 150)
            .Select(i => PipelineIssue.Warning(PipelineStage.Metadata, "doc1.pdf", $"Optional field {i} absent."));
        var error = PipelineIssue.Error(PipelineStage.TextQuality, "doc1.pdf", "corrupted content");
        // Sorted the same way PdfPipelineValidator.Validate does (errors ahead of
        // warnings) - this test targets BuildExtractionOutput's cap, not the sort itself.
        var sortedIssues = new[] { error }.Concat(warnings).ToList();

        var validator = new Mock<IPdfPipelineValidator>();
        validator.Setup(v => v.Validate(It.IsAny<IReadOnlyList<PdfExtractionResult>>(), It.IsAny<PdfCleanResult>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .Returns(new PdfQualityGateResult { Passed = true, CleanedRecords = 1, Issues = sortedIssues });
        var env = MockEnvironmentImpl("Production");

        var pipeline = BuildPipeline(blobStore, reportWriter, extractor, cleaner, validator, env);

        var output = await pipeline.ExtractDocumentsAsync(Entries("doc1.pdf"));

        Assert.AreEqual(100, output.Issues.Count);
        Assert.IsTrue(output.Issues.Any(i => i.IsError && i.Stage == PipelineStage.TextQuality));
    }

    [TestMethod]
    public async Task BlobNotInSourceIdsToProcess_NeverExtracted()
    {
        var blobStore    = MockBlobStore();
        var reportWriter = MockReportWriter();
        var extractor    = MockExtractor("doc1.pdf");
        var cleaner      = new Mock<IPdfCleaner>();
        cleaner.Setup(c => c.CleanPdf(It.IsAny<IReadOnlyList<PdfPageRecord>>())).Returns(new PdfCleanResult());
        var validator = new Mock<IPdfPipelineValidator>();
        validator.Setup(v => v.Validate(It.IsAny<IReadOnlyList<PdfExtractionResult>>(), It.IsAny<PdfCleanResult>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .Returns(new PdfQualityGateResult { Passed = true, CleanedRecords = 0 });
        var env = MockEnvironmentImpl("Production");

        var pipeline = BuildPipeline(blobStore, reportWriter, extractor, cleaner, validator, env);

        // sourceIdsToProcess deliberately excludes doc1.pdf - already up to date per the caller's diff.
        await pipeline.ExtractDocumentsAsync(new Dictionary<string, PdfBlobInfo>());

        extractor.Verify(e => e.ExtractPDFAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task EmptySourceIdsToProcess_ReturnsEmptyOutput_DoesNotThrow()
    {
        // Regression test: the normal steady-state run where nothing is new/updated
        // (ExtractionService's own diff already found zero documents to process) must
        // complete cleanly with an empty PdfExtractionOutput, not throw - covers both the
        // Parallel.ForEachAsync loop over an empty dictionary and EvaluateGate's
        // attemptedPages == 0 -> pass path (finding #5) via the real, unmocked validator.
        var blobStore    = MockBlobStore();
        var reportWriter = MockReportWriter();
        var extractor    = new Mock<IPdfExtractor>();
        var cleaner      = new Mock<IPdfCleaner>();
        var env          = MockEnvironmentImpl("Production");

        // Real validator (not the usual mock) so this actually exercises EvaluateGate's
        // empty-run path end to end, rather than a stubbed "Passed = true".
        var pipeline = new PdfExtractionPipeline(
            new Mock<BlobContainerClient>().Object, new Mock<BlobContainerClient>().Object,
            blobStore.Object, reportWriter.Object, extractor.Object, cleaner.Object, new PdfPipelineValidator(),
            env.Object, NullLogger<PdfExtractionPipeline>.Instance);

        var output = await pipeline.ExtractDocumentsAsync(new Dictionary<string, PdfBlobInfo>());

        Assert.AreEqual(0, output.Docs.Count);
        Assert.AreEqual(0, output.ValidationErrors);
        Assert.AreEqual(0, output.ReconciliationProblems);
        extractor.Verify(e => e.ExtractPDFAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Never);
        cleaner.Verify(c => c.CleanPdf(It.IsAny<IReadOnlyList<PdfPageRecord>>()), Times.Never);
    }

    [TestMethod]
    public async Task ValidationFailed_OnErrorRateAlone_WarningReportsErrorCount()
    {
        // Regression test for finding #19: a run can fail validation purely on error rate, with
        // zero reconciliation problems - the message must say so, not just report a
        // misleadingly-reassuring "(0 reconciliation problem(s))".
        //
        // Validation is reported, not enforced (PdfExtractionPipeline step 4), so this asserts
        // the warning's content and that the run continued. It previously asserted an abort;
        // that expectation outlived the behaviour change.
        var blobStore    = MockBlobStore();
        var reportWriter = MockReportWriter();
        var extractor    = MockExtractor("doc1.pdf");
        var cleaner      = new Mock<IPdfCleaner>();
        cleaner.Setup(c => c.CleanPdf(It.IsAny<IReadOnlyList<PdfPageRecord>>())).Returns(OneRecordCleanResult("doc1.pdf"));
        var validator = new Mock<IPdfPipelineValidator>();
        validator.Setup(v => v.Validate(It.IsAny<IReadOnlyList<PdfExtractionResult>>(), It.IsAny<PdfCleanResult>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .Returns(new PdfQualityGateResult
            {
                Passed = false,
                ReconciliationProblems = [],
                Issues = [PipelineIssue.Error(PipelineStage.TextQuality, "doc1.pdf", "corrupted content")],
            });
        var env    = MockEnvironmentImpl("Production");
        var logger = new CapturingLogger<PdfExtractionPipeline>();

        var pipeline = BuildPipeline(blobStore, reportWriter, extractor, cleaner, validator, env, logger: logger);

        var output = await pipeline.ExtractDocumentsAsync(Entries("doc1.pdf"));

        // The run proceeds to indexing regardless - validation informs, it does not gate.
        Assert.IsNotNull(output);

        var warning = logger.Entries.SingleOrDefault(e => e.Level == LogLevel.Warning
                                                       && e.Message.Contains("PDF validation failed"));
        Assert.IsNotNull(warning.Message, "expected a validation-failed warning to be logged");
        StringAssert.Contains(warning.Message, "0 reconciliation problem(s)");
        StringAssert.Contains(warning.Message, "1 error-severity issue(s)");
    }

    [TestMethod]
    public async Task ValidationFailed_NotDevelopment_ContinuesAndStillWritesReport()
    {
        // Outside Development used to abort the run. It no longer does: validation is reported,
        // not enforced, in every environment (PdfExtractionPipeline step 4). What must still
        // hold is that the failure is recorded rather than swallowed - hence the report assert.
        var blobStore    = MockBlobStore();
        var reportWriter = MockReportWriter();
        var extractor    = MockExtractor("doc1.pdf");
        var cleaner      = new Mock<IPdfCleaner>();
        cleaner.Setup(c => c.CleanPdf(It.IsAny<IReadOnlyList<PdfPageRecord>>())).Returns(OneRecordCleanResult("doc1.pdf"));
        var validator = new Mock<IPdfPipelineValidator>();
        validator.Setup(v => v.Validate(It.IsAny<IReadOnlyList<PdfExtractionResult>>(), It.IsAny<PdfCleanResult>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .Returns(new PdfQualityGateResult { Passed = false, ReconciliationProblems = ["mismatch"] });
        var env    = MockEnvironmentImpl("Production");
        var logger = new CapturingLogger<PdfExtractionPipeline>();

        var pipeline = BuildPipeline(blobStore, reportWriter, extractor, cleaner, validator, env, logger: logger);

        var output = await pipeline.ExtractDocumentsAsync(Entries("doc1.pdf"));

        Assert.IsNotNull(output);
        Assert.IsTrue(logger.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains("PDF validation failed")));
        reportWriter.Verify(w => w.WriteReportAsync(
            It.Is<string>(p => p.Contains("pdf-validation")), It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task ValidationFailed_Development_ContinuesAndReturnsDocs()
    {
        var blobStore    = MockBlobStore();
        var reportWriter = MockReportWriter();
        var extractor    = MockExtractor("doc1.pdf");
        var cleaner      = new Mock<IPdfCleaner>();
        cleaner.Setup(c => c.CleanPdf(It.IsAny<IReadOnlyList<PdfPageRecord>>())).Returns(OneRecordCleanResult("doc1.pdf"));
        var validator = new Mock<IPdfPipelineValidator>();
        validator.Setup(v => v.Validate(It.IsAny<IReadOnlyList<PdfExtractionResult>>(), It.IsAny<PdfCleanResult>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .Returns(new PdfQualityGateResult { Passed = false, ReconciliationProblems = ["mismatch"], CleanedRecords = 1 });
        var env = MockEnvironmentImpl("Development");

        var pipeline = BuildPipeline(blobStore, reportWriter, extractor, cleaner, validator, env);

        var output = await pipeline.ExtractDocumentsAsync(Entries("doc1.pdf"));

        Assert.AreEqual(1, output.Docs.Count);
    }

    [TestMethod]
    public async Task ReportWriterDisabled_NoReportBlobsWritten()
    {
        var blobStore    = MockBlobStore();
        var reportWriter = MockReportWriter(isEnabled: false);
        var extractor    = MockExtractor("doc1.pdf");
        var cleaner      = new Mock<IPdfCleaner>();
        cleaner.Setup(c => c.CleanPdf(It.IsAny<IReadOnlyList<PdfPageRecord>>())).Returns(OneRecordCleanResult("doc1.pdf"));
        var validator = new Mock<IPdfPipelineValidator>();
        validator.Setup(v => v.Validate(It.IsAny<IReadOnlyList<PdfExtractionResult>>(), It.IsAny<PdfCleanResult>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .Returns(new PdfQualityGateResult { Passed = true, CleanedRecords = 1 });
        var env = MockEnvironmentImpl("Production");

        var pipeline = BuildPipeline(blobStore, reportWriter, extractor, cleaner, validator, env);

        await pipeline.ExtractDocumentsAsync(Entries("doc1.pdf"));

        reportWriter.Verify(w => w.WriteReportAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task FileFactsReport_IncludesEstimatedCostUsd()
    {
        // Regression test for finding #10: EstimatedCostUsd was computed per file and then
        // dropped entirely - never reaching file-facts.json. SuccessResult sets it to 0.01m.
        var blobStore    = MockBlobStore();
        var reportWriter = MockReportWriter();
        object? fileFactsPayload = null;
        reportWriter.Setup(w => w.WriteReportAsync(
                It.Is<string>(p => p.Contains("file-facts")), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Callback<string, object, CancellationToken>((_, payload, _) => fileFactsPayload = payload)
            .Returns(Task.CompletedTask);
        var extractor = MockExtractor("doc1.pdf");
        var cleaner   = new Mock<IPdfCleaner>();
        cleaner.Setup(c => c.CleanPdf(It.IsAny<IReadOnlyList<PdfPageRecord>>())).Returns(OneRecordCleanResult("doc1.pdf"));
        var validator = new Mock<IPdfPipelineValidator>();
        validator.Setup(v => v.Validate(It.IsAny<IReadOnlyList<PdfExtractionResult>>(), It.IsAny<PdfCleanResult>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .Returns(new PdfQualityGateResult { Passed = true, CleanedRecords = 1 });
        var env = MockEnvironmentImpl("Production");

        var pipeline = BuildPipeline(blobStore, reportWriter, extractor, cleaner, validator, env);

        await pipeline.ExtractDocumentsAsync(Entries("doc1.pdf"));

        Assert.IsNotNull(fileFactsPayload);
        var json = System.Text.Json.JsonSerializer.Serialize(fileFactsPayload);
        StringAssert.Contains(json, "\"EstimatedCostUsd\":0.01");
    }

    [TestMethod]
    public async Task CorpusWallClockLimitExceeded_StopsSubmittingNewFiles_WithoutFailingValidation()
    {
        // Regression test for finding #11: once the corpus wall-clock limit is exceeded, a
        // file must not be submitted (elapsed time since runAt already exceeds a negative
        // limit) and must not be recorded as an error - it should look like a clean,
        // partial run, not a validation failure, so the next run picks it up normally.
        var blobStore    = MockBlobStore();
        var reportWriter = MockReportWriter();
        var extractor    = MockExtractor("doc1.pdf");
        var cleaner      = new Mock<IPdfCleaner>();
        cleaner.Setup(c => c.CleanPdf(It.IsAny<IReadOnlyList<PdfPageRecord>>())).Returns(new PdfCleanResult());
        var validator = new Mock<IPdfPipelineValidator>();
        validator.Setup(v => v.Validate(It.IsAny<IReadOnlyList<PdfExtractionResult>>(), It.IsAny<PdfCleanResult>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .Returns(new PdfQualityGateResult { Passed = true, CleanedRecords = 0 });
        var env = MockEnvironmentImpl("Production");

        var pipeline = BuildPipeline(blobStore, reportWriter, extractor, cleaner, validator, env,
            corpusWallClockLimit: TimeSpan.FromSeconds(-1));

        var output = await pipeline.ExtractDocumentsAsync(Entries("doc1.pdf"));

        Assert.AreEqual(0, output.Docs.Count);
        extractor.Verify(e => e.ExtractPDFAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Never);
        blobStore.Verify(s => s.DownloadBytesAsync(It.IsAny<BlobContainerClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task OversizedContentLengthFromListing_RejectedWithoutDownloading()
    {
        // Regression test for finding #20: ContentLength is already known from the cheap
        // blob listing - an over-limit file should never reach DownloadBytesAsync just to
        // have PdfDocumentValidator reject it after the fact.
        var blobStore    = MockBlobStore();
        var reportWriter = MockReportWriter();
        var extractor    = new Mock<IPdfExtractor>();
        var cleaner      = new Mock<IPdfCleaner>();
        cleaner.Setup(c => c.CleanPdf(It.IsAny<IReadOnlyList<PdfPageRecord>>())).Returns(new PdfCleanResult());
        var validator = new Mock<IPdfPipelineValidator>();
        validator.Setup(v => v.Validate(It.IsAny<IReadOnlyList<PdfExtractionResult>>(), It.IsAny<PdfCleanResult>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .Returns(new PdfQualityGateResult { Passed = true, CleanedRecords = 0 });
        var env = MockEnvironmentImpl("Production");

        var pipeline = BuildPipeline(blobStore, reportWriter, extractor, cleaner, validator, env);
        var entries = new Dictionary<string, PdfBlobInfo>
        {
            ["big.pdf"] = new PdfBlobInfo(DateTimeOffset.UtcNow, PdfDocumentValidator.MaxBytes + 1, ZenyaMetadata.Empty),
        };

        var output = await pipeline.ExtractDocumentsAsync(entries);

        Assert.AreEqual(0, output.Docs.Count);
        blobStore.Verify(s => s.DownloadBytesAsync(It.IsAny<BlobContainerClient>(), "big.pdf", It.IsAny<CancellationToken>()), Times.Never);
        extractor.Verify(e => e.ExtractPDFAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task ExtractorThrowsForOneBlob_RecordedAsFileLevelError_RunStillSucceeds()
    {
        var blobStore    = MockBlobStore();
        var reportWriter = MockReportWriter();
        var extractor    = new Mock<IPdfExtractor>();
        extractor.Setup(e => e.ExtractPDFAsync("doc1.pdf", It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var cleaner = new Mock<IPdfCleaner>();
        cleaner.Setup(c => c.CleanPdf(It.IsAny<IReadOnlyList<PdfPageRecord>>())).Returns(new PdfCleanResult());
        var validator = new Mock<IPdfPipelineValidator>();
        validator.Setup(v => v.Validate(It.IsAny<IReadOnlyList<PdfExtractionResult>>(), It.IsAny<PdfCleanResult>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .Returns(new PdfQualityGateResult { Passed = true, CleanedRecords = 0 });
        var env = MockEnvironmentImpl("Production");

        var pipeline = BuildPipeline(blobStore, reportWriter, extractor, cleaner, validator, env);

        // A per-file exception must not abort the whole run - it becomes a file-level error instead.
        var output = await pipeline.ExtractDocumentsAsync(Entries("doc1.pdf"));

        Assert.AreEqual(0, output.Docs.Count);
    }

    // --- BuildPageContextLookup --------------------------------------------------------

    private static PdfExtractionResult ResultWithStructure(
        string blobName, PdfDocumentStructure? structure, bool ok = true,
        IReadOnlyDictionary<int, string>? breadcrumbs = null) => new(
        Ok: ok, BlobName: blobName, FileSizeBytes: 100, PdfSpecVersion: 1.7,
        NativeMetadata: null, RawContent: null,
        Pages: ok ? [new PdfPageRecord { BlobName = blobName, PageNumber = 1, PageContent = "x", Title = "t" }] : null,
        Structure: structure, EstimatedCostUsd: null,
        Error: ok ? null : PipelineIssue.Error(PipelineStage.ParsePages, blobName, "boom"))
    {
        SectionBreadcrumbs = breadcrumbs ?? new Dictionary<int, string>(),
    };

    private static PdfDocumentStructure EmptyStructure() => new(
        Headings: [], Boilerplate: [], Tables: [], PageDimensions: [], SelectionMarks: [], Figures: [], Lines: [], Sections: []);

    // ── BuildDocuments: page -> document assembly (action-plan.md C8) ─────────
    // These replace the old BuildPageContextLookup tests. That method filtered structure
    // down to each page; there is no per-page record any more, so the thing worth testing
    // is the assembly itself - specifically that PageSpans stay exact, since every offset
    // downstream is measured against them.

    private static PdfCleanResult CleanedPages(params (string Blob, int Page, string Content)[] pages)
    {
        var result = new PdfCleanResult();
        foreach (var (blob, page, content) in pages)
            result.AddRecord(new CleanedPdfPageRecord
            {
                BlobName = blob, PageNumber = page, PageContent = content, Title = "T",
            });
        return result;
    }

    [TestMethod]
    public void BuildDocuments_OnePerBlob_NotOnePerPage()
    {
        var docs = PdfExtractionPipeline.BuildDocuments(
            [SuccessResult("a.pdf"), SuccessResult("b.pdf")],
            CleanedPages(("a.pdf", 1, "one"), ("a.pdf", 2, "two"), ("b.pdf", 1, "three")),
            [], []);

        Assert.AreEqual(2, docs.Count);
        Assert.AreEqual(2, docs.Single(d => d.SourceId == "a.pdf").PageSpans.Count);
    }

    [TestMethod]
    public void BuildDocuments_PageSpansAddressTheAssembledContentExactly()
    {
        var docs = PdfExtractionPipeline.BuildDocuments(
            [SuccessResult("a.pdf")],
            CleanedPages(("a.pdf", 1, "alpha"), ("a.pdf", 2, "beta"), ("a.pdf", 3, "gamma")),
            [], []);

        var doc = docs.Single();

        // The whole point of recording spans during assembly rather than reconstructing
        // them: slicing Content at a span must return that page's text back verbatim,
        // including for pages after the first, where the separator has shifted everything.
        foreach (var (span, expected) in doc.PageSpans.Zip(new[] { "alpha", "beta", "gamma" }))
            Assert.AreEqual(expected, doc.Content.Substring(span.Offset, span.Length));
    }

    [TestMethod]
    public void BuildDocuments_PagesAreOrderedByPageNumber_NotInputOrder()
    {
        var docs = PdfExtractionPipeline.BuildDocuments(
            [SuccessResult("a.pdf")],
            CleanedPages(("a.pdf", 3, "third"), ("a.pdf", 1, "first"), ("a.pdf", 2, "second")),
            [], []);

        var doc = docs.Single();

        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, doc.PageSpans.Select(s => s.PageNumber).ToArray());
        Assert.IsTrue(doc.Content.StartsWith("first", StringComparison.Ordinal));
    }

    [TestMethod]
    public void BuildDocuments_CarriesRoutingAndLanguage()
    {
        // Both were computed at extraction and read by nothing at all before C7.
        var result = SuccessResult("a.pdf") with { Language = "nl" };

        var docs = PdfExtractionPipeline.BuildDocuments(
            [result], CleanedPages(("a.pdf", 1, "text")), [], []);

        Assert.AreEqual("nl", docs.Single().Language);
    }

    [TestMethod]
    public void BuildDocuments_BlobWithNoCleanedPages_ProducesNoDocument()
    {
        // "Extraction produced nothing for this file" is a real outcome the validation
        // report already covers. An empty-content document would chunk to zero chunks while
        // looking like a successfully processed file.
        var docs = PdfExtractionPipeline.BuildDocuments(
            [SuccessResult("a.pdf")], CleanedPages(), [], []);

        Assert.AreEqual(0, docs.Count);
    }

    [TestMethod]
    public void BuildDocuments_BlankPage_GetsNoSeparator_ButKeepsItsSpan()
    {
        // A caption-less diagram page cleans to nothing (PdfCleaner.ConvertFigures drops the
        // placeholder) and is kept as a record rather than dropped, so this is the ordinary
        // case for a picture-only page, not an edge case.
        var clean = new PdfCleanResult();
        clean.AddRecord(new CleanedPdfPageRecord { BlobName = "a.pdf", PageNumber = 1, PageContent = "alpha", Title = "T" });
        clean.AddRecord(new CleanedPdfPageRecord { BlobName = "a.pdf", PageNumber = 2, PageContent = "", Title = "T", IsPictureOnlyPage = true });
        clean.AddRecord(new CleanedPdfPageRecord { BlobName = "a.pdf", PageNumber = 3, PageContent = "gamma", Title = "T" });

        var doc = PdfExtractionPipeline.BuildDocuments([SuccessResult("a.pdf")], clean, [], []).Single();

        // Separating on both sides of nothing would leave a four-newline run. PdfCleaner
        // collapses \n{3,} per page, but assembly is the only stage after cleaning and
        // nothing re-collapses the joined text - so this is the one place such a run can
        // reach the index.
        Assert.AreEqual("alpha\n\ngamma", doc.Content);

        // The page is still recorded. Dropping the span would drop IsPictureOnly with it,
        // which is the only signal that a mostly-normal document has diagram pages in it.
        var blank = doc.PageSpans.Single(s => s.PageNumber == 2);
        Assert.AreEqual(0, blank.Length);
        Assert.IsTrue(blank.IsPictureOnly);

        // And the pages either side still address Content exactly, blank page in between.
        foreach (var (span, expected) in doc.PageSpans.Zip(new[] { "alpha", "", "gamma" }))
            Assert.AreEqual(expected, doc.Content.Substring(span.Offset, span.Length));
    }

    [TestMethod]
    public void BuildDocuments_BlobNamesDifferingOnlyByCase_AreTwoDocuments()
    {
        // Azure blob names are case-sensitive, so this pair can legally sit in one container.
        // Under a case-insensitive comparer the lookups here threw ArgumentException on the
        // duplicate key - and threw it after every paid Document Intelligence call in the run
        // had already been made.
        var docs = PdfExtractionPipeline.BuildDocuments(
            [SuccessResult("Beleid.pdf"), SuccessResult("beleid.pdf")],
            CleanedPages(("Beleid.pdf", 1, "upper"), ("beleid.pdf", 1, "lower")),
            [], []);

        Assert.AreEqual(2, docs.Count);
        Assert.AreEqual("upper", docs.Single(d => d.SourceId == "Beleid.pdf").Content);
        Assert.AreEqual("lower", docs.Single(d => d.SourceId == "beleid.pdf").Content);
    }
}
