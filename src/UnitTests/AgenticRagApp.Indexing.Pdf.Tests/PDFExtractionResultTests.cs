using Microsoft.VisualStudio.TestTools.UnitTesting;
using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Common.Models;

namespace RagApp.UnitTests.PdfExtraction;

[TestClass]
public class PDFExtractionResultTests
{
    [TestMethod]
    public void OkTrue_WithError_ThrowsAtConstruction()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new PDFExtractionResult(
            Ok: true, BlobName: "doc1.pdf", FileSizeBytes: 1024, PdfSpecVersion: null,
            NativeMetadata: null, RawContent: null, Pages: [], Structure: null,
            EstimatedCostUsd: null, Error: new ExtractionError(RowNumber: 0, DocumentId: "doc1.pdf", Message: "boom")));
    }

    [TestMethod]
    public void OkFalse_WithNoError_ThrowsAtConstruction()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new PDFExtractionResult(
            Ok: false, BlobName: "doc1.pdf", FileSizeBytes: 1024, PdfSpecVersion: null,
            NativeMetadata: null, RawContent: null, Pages: null, Structure: null,
            EstimatedCostUsd: null, Error: null));
    }

    [TestMethod]
    public void OkTrue_WithNullPages_ThrowsAtConstruction()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new PDFExtractionResult(
            Ok: true, BlobName: "doc1.pdf", FileSizeBytes: 1024, PdfSpecVersion: null,
            NativeMetadata: null, RawContent: null, Pages: null, Structure: null,
            EstimatedCostUsd: null, Error: null));
    }

    [TestMethod]
    public void OkTrue_WithPagesAndNoError_ConstructsFine()
    {
        var result = new PDFExtractionResult(
            Ok: true, BlobName: "doc1.pdf", FileSizeBytes: 1024, PdfSpecVersion: 1.7,
            NativeMetadata: null, RawContent: null, Pages: [], Structure: null,
            EstimatedCostUsd: null, Error: null);

        Assert.IsTrue(result.Ok);
    }

    [TestMethod]
    public void OkFalse_WithError_ConstructsFine()
    {
        var result = new PDFExtractionResult(
            Ok: false, BlobName: "doc1.pdf", FileSizeBytes: 1024, PdfSpecVersion: null,
            NativeMetadata: null, RawContent: null, Pages: null, Structure: null,
            EstimatedCostUsd: null, Error: new ExtractionError(RowNumber: 0, DocumentId: "doc1.pdf", Message: "boom"));

        Assert.IsFalse(result.Ok);
    }
}
