using Azure.AI.ContentUnderstanding;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Infrastructure.Clients.Blob;
using AgenticRagApp.Infrastructure.Clients.ContentUnderstanding;
using AgenticRagApp.Infrastructure.Clients.Language;
using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Services;
using AgenticRagApp.Observability.Reports;

namespace RagApp.UnitTests.PdfExtraction;

// The `language` producer, at the hop that wires it: ExtractFileAsync. Everything else about
// extraction is covered in ExtractionServiceTests, which substitutes that method - so this is
// the one place the real per-file path runs, with the paid analyze call mocked.
//
// Why it is tested at all: Content Understanding reports no language, the index field is
// filterable and facetable, and it was null end to end for weeks without anything noticing.
[TestClass]
public class ExtractionLanguageTests
{
    [TestMethod]
    public async Task ADetectedLanguageAndItsConfidenceLandOnTheExtractedFile()
    {
        var detector = DetectorReturning(new DocumentLanguage("nl", 0.97));

        var extracted = await Service(detector).ExtractFileAsync("a.pdf", default);

        Assert.IsTrue(extracted.Ok);
        Assert.AreEqual("nl", extracted.Language);
        Assert.AreEqual(0.97, extracted.LanguageConfidence!.Value, 1e-9);
    }

    [TestMethod]
    public async Task AnUndetectedLanguageLeavesTheFieldNull_NotBlank()
    {
        var extracted = await Service(DetectorReturning(null)).ExtractFileAsync("a.pdf", default);

        Assert.IsTrue(extracted.Ok);
        Assert.IsNull(extracted.Language);
        Assert.IsNull(extracted.LanguageConfidence);
    }

    [TestMethod]
    public async Task TheDetectorIsSentOneBoundedSample_NotTheWholeDocument()
    {
        // One call per document on a sample - never the full markdown of a 134-page document,
        // and never per page. The sample size matches IdentityTagger's on purpose.
        var seen = new List<string>();
        var detector = new Mock<IDocumentLanguageDetector>();
        detector.Setup(d => d.DetectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string sample, CancellationToken _) => seen.Add(sample))
            .ReturnsAsync(new DocumentLanguage("nl", 0.9));

        await Service(detector, markdown: new string('a', 20_000)).ExtractFileAsync("a.pdf", default);

        Assert.AreEqual(1, seen.Count);
        Assert.AreEqual(1_200, seen[0].Length);
    }

    [TestMethod]
    public async Task AFailedAnalysisIsNeverLanguageDetected()
    {
        // There is no text to detect from, and a document that never extracted has nothing to
        // stamp a language onto. Strict mock: any call fails the test.
        var detector = new Mock<IDocumentLanguageDetector>(MockBehavior.Strict);

        var extracted = await Service(detector, markdown: null).ExtractFileAsync("a.pdf", default);

        Assert.IsFalse(extracted.Ok);
        Assert.IsNull(extracted.Language);
    }

    [TestMethod]
    public async Task WithNoDetectorRegistered_ExtractionStillSucceedsWithNoLanguage()
    {
        // The pre-2026-09-08 behaviour, kept reachable: the dependency is optional, and its
        // absence must degrade rather than throw.
        var extracted = await Service(languageDetector: null).ExtractFileAsync("a.pdf", default);

        Assert.IsTrue(extracted.Ok);
        Assert.IsNull(extracted.Language);
    }

    private static Mock<IDocumentLanguageDetector> DetectorReturning(DocumentLanguage? language)
    {
        var detector = new Mock<IDocumentLanguageDetector>();
        detector.Setup(d => d.DetectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(language);
        return detector;
    }

    private static ExtractionService Service(
        Mock<IDocumentLanguageDetector>? languageDetector, string? markdown = "Dit is een Nederlands document.") =>
        Service(languageDetector?.Object, markdown);

    // The real service with only the paid call and the blob download mocked. markdown: null
    // stands for a response carrying no DocumentContent, which ExtractFileAsync records as a
    // failed file.
    private static ExtractionService Service(
        IDocumentLanguageDetector? languageDetector, string? markdown)
    {
        var blobStore = new Mock<IBlobStore>();
        blobStore.Setup(s => s.DownloadBytesAsync(
                It.IsAny<BlobContainerClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([1, 2, 3]);

        var analysis = new ContentAnalysis(
            ContentUnderstandingModelFactory.AnalysisResult(
                analyzerId: "prebuilt-documentSearch",
                apiVersion: "2025-11-01",
                createdAt: default,
                warnings: null,
                stringEncoding: "utf16",
                contents: markdown is null
                    ? []
                    : [ContentUnderstandingModelFactory.DocumentContent(markdown: markdown)]),
            Usage: null);

        var analysisClient = new Mock<IContentAnalysisClient>();
        analysisClient.Setup(c => c.AnalyzeAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(analysis);

        var reportWriter = new Mock<IRunReportWriter>();
        reportWriter.SetupGet(w => w.IsEnabled).Returns(false);

        return new ExtractionService(
            new Mock<IIndexDiffService>().Object,
            new Mock<BlobContainerClient>().Object,
            analysisClient.Object,
            new Mock<BlobContainerClient>().Object,
            blobStore.Object,
            new ExtractionReporter(reportWriter.Object, NullLogger<ExtractionReporter>.Instance),
            NullLogger<ExtractionService>.Instance,
            languageDetector: languageDetector);
    }
}
