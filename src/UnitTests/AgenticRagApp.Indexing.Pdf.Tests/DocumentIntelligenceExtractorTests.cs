using System.Text;
using Azure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using AgenticRagApp.Infrastructure.Clients.DocumentIntelligence;
using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Indexing.Pdf.Services;
using AgenticRagApp.Common.Models;

namespace RagApp.UnitTests.PdfExtraction;

[TestClass]
public class DocumentIntelligenceExtractorTests
{
    // Same construction approach as PdfDocumentValidatorTests.BuildMinimalPdf - a real,
    // structurally valid PDF so Step 1 (preflight) passes and the extractor proceeds to
    // Step 2/3, without needing a genuine PDF fixture file on disk.
    private static byte[] BuildMinimalPdf(int pageCount = 1)
    {
        var sb      = new StringBuilder();
        var offsets = new List<int>();

        void AppendObj(string content)
        {
            offsets.Add(sb.Length);
            sb.Append(content);
        }

        sb.Append("%PDF-1.7\n");

        var kids = string.Join(" ", Enumerable.Range(0, pageCount).Select(i => $"{3 + i} 0 R"));
        AppendObj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        AppendObj($"2 0 obj\n<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>\nendobj\n");
        for (var i = 0; i < pageCount; i++)
            AppendObj($"{3 + i} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");

        var xrefOffset   = sb.Length;
        var totalObjects = offsets.Count + 1;

        sb.Append($"xref\n0 {totalObjects}\n");
        sb.Append("0000000000 65535 f \n");
        foreach (var off in offsets)
            sb.Append($"{off:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {totalObjects} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static PDFExtractor BuildExtractor(IDocumentAnalysisClient diClient) =>
        new(new PdfDocumentIntelligenceAnalyzer(diClient, NullLogger<PdfDocumentIntelligenceAnalyzer>.Instance));

    [TestMethod]
    public async Task InvalidPdf_FailsAtPreflight_NeverCallsDocumentIntelligence()
    {
        var diClient = new Mock<IDocumentAnalysisClient>();
        var extractor = BuildExtractor(diClient.Object);

        var result = await extractor.ExtractPDFAsync("doc.pdf", []);

        Assert.IsFalse(result.Ok);
        Assert.AreEqual(PdfOpenFailureReason.EmptyFile, result.Error!.Reason);
        Assert.IsNull(result.NativeMetadata);
        Assert.AreEqual(1, result.ValidationDiagnostics.Errors.Count);
        diClient.Verify(c => c.SubmitAnalyzeAsync(It.IsAny<Azure.AI.DocumentIntelligence.AnalyzeDocumentOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task ValidPdf_DocumentIntelligenceSubmissionFails_ReturnsTypedErrorButKeepsNativeMetadata()
    {
        var diClient = new Mock<IDocumentAnalysisClient>();
        diClient.Setup(c => c.SubmitAnalyzeAsync(It.IsAny<Azure.AI.DocumentIntelligence.AnalyzeDocumentOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(429, "throttled"));
        var extractor = BuildExtractor(diClient.Object);
        var pdfBytes  = BuildMinimalPdf();

        var result = await extractor.ExtractPDFAsync("doc.pdf", pdfBytes);

        Assert.IsFalse(result.Ok);
        Assert.AreEqual(PdfOpenFailureReason.Throttled, result.Error!.Reason);
        // Native metadata (PdfPig-derived) is still captured before the DI call fails -
        // it doesn't depend on DI succeeding.
        Assert.IsNotNull(result.NativeMetadata);
        Assert.AreEqual(1, result.DocumentIntelligenceDiagnostics.Errors.Count);
    }

    [TestMethod]
    public async Task Name_IsDocumentIntelligence()
    {
        var extractor = BuildExtractor(new Mock<IDocumentAnalysisClient>().Object);

        Assert.AreEqual("DocumentIntelligence", extractor.Name);
    }
}
