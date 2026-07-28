using Microsoft.VisualStudio.TestTools.UnitTesting;
using AgenticRagApp.Indexing.Pdf.Models;

namespace RagApp.UnitTests.PdfExtraction;

[TestClass]
public class PdfExtractionDiagnosticsTests
{
    [TestMethod]
    public void Constructor_SetsAllProperties()
    {
        var diagnostics = new PdfExtractionDiagnostics
        {
            BlobName                 = "doc.pdf",
            DominantFontSize         = 11.5,
            DominantPageWidth        = 612.0,
            KnownSectionCount        = 3,
            BookmarksContributed     = true,
            DecorationDetectionRan   = true,
            PagesWithDecorationRemoved = 2,
            ParsedTitle              = "Title",
            ParsedVersion            = "1.7",
            ParsedPublicationDateRaw = "2024-01-01",
            PageCount                = 10,
            PageErrorCount           = 1,
            WarningCount             = 2,
        };

        Assert.AreEqual("doc.pdf", diagnostics.BlobName);
        Assert.AreEqual(11.5, diagnostics.DominantFontSize);
        Assert.AreEqual(612.0, diagnostics.DominantPageWidth);
        Assert.AreEqual(3, diagnostics.KnownSectionCount);
        Assert.IsTrue(diagnostics.BookmarksContributed);
        Assert.IsTrue(diagnostics.DecorationDetectionRan);
        Assert.AreEqual(2, diagnostics.PagesWithDecorationRemoved);
        Assert.AreEqual("Title", diagnostics.ParsedTitle);
        Assert.AreEqual("1.7", diagnostics.ParsedVersion);
        Assert.AreEqual("2024-01-01", diagnostics.ParsedPublicationDateRaw);
        Assert.AreEqual(10, diagnostics.PageCount);
        Assert.AreEqual(1, diagnostics.PageErrorCount);
        Assert.AreEqual(2, diagnostics.WarningCount);
    }

    [TestMethod]
    public void Constructor_OnlyBlobNameRequired_OthersDefaultToFalsyValues()
    {
        var diagnostics = new PdfExtractionDiagnostics { BlobName = "doc.pdf" };

        Assert.AreEqual("doc.pdf", diagnostics.BlobName);
        Assert.IsFalse(diagnostics.BookmarksContributed);
        Assert.IsFalse(diagnostics.DecorationDetectionRan);
        Assert.IsNull(diagnostics.ParsedTitle);
        Assert.IsNull(diagnostics.ParsedVersion);
        Assert.IsNull(diagnostics.ParsedPublicationDateRaw);
        Assert.AreEqual(0, diagnostics.PageCount);
    }
}
