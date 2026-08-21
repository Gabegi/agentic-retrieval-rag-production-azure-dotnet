using Microsoft.VisualStudio.TestTools.UnitTesting;
using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Common.Models;

namespace RagApp.UnitTests.PdfExtraction;

[TestClass]
public class PdfExtractionResultTests
{
    [TestMethod]
    public void OkTrue_WithError_ThrowsAtConstruction()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new PdfExtractionResult(
            Ok: true, BlobName: "doc1.pdf", FileSizeBytes: 1024, PdfSpecVersion: null,
            NativeMetadata: null, RawContent: null, Pages: [], Structure: null,
            EstimatedCostUsd: null, Error: PipelineIssue.Error(PipelineStage.ParsePages, "doc1.pdf", "boom")));
    }

    [TestMethod]
    public void OkFalse_WithNoError_ThrowsAtConstruction()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new PdfExtractionResult(
            Ok: false, BlobName: "doc1.pdf", FileSizeBytes: 1024, PdfSpecVersion: null,
            NativeMetadata: null, RawContent: null, Pages: null, Structure: null,
            EstimatedCostUsd: null, Error: null));
    }

    [TestMethod]
    public void OkTrue_WithNullPages_ThrowsAtConstruction()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new PdfExtractionResult(
            Ok: true, BlobName: "doc1.pdf", FileSizeBytes: 1024, PdfSpecVersion: null,
            NativeMetadata: null, RawContent: null, Pages: null, Structure: null,
            EstimatedCostUsd: null, Error: null));
    }

    [TestMethod]
    public void OkTrue_WithPagesAndNoError_ConstructsFine()
    {
        var result = new PdfExtractionResult(
            Ok: true, BlobName: "doc1.pdf", FileSizeBytes: 1024, PdfSpecVersion: 1.7,
            NativeMetadata: null, RawContent: null, Pages: [], Structure: null,
            EstimatedCostUsd: null, Error: null);

        Assert.IsTrue(result.Ok);
    }

    [TestMethod]
    public void OkFalse_WithError_ConstructsFine()
    {
        var result = new PdfExtractionResult(
            Ok: false, BlobName: "doc1.pdf", FileSizeBytes: 1024, PdfSpecVersion: null,
            NativeMetadata: null, RawContent: null, Pages: null, Structure: null,
            EstimatedCostUsd: null, Error: PipelineIssue.Error(PipelineStage.ParsePages, "doc1.pdf", "boom"));

        Assert.IsFalse(result.Ok);
    }
}
