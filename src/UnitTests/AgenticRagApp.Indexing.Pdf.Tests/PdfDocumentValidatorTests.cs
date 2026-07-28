using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Indexing.Pdf.Services;
using AgenticRagApp.Common.Models;

namespace RagApp.UnitTests.PdfExtraction;

[TestClass]
public class PdfDocumentValidatorTests
{
    // Builds a minimal, structurally valid PDF byte array with a controllable page count
    // and spec version - byte offsets for the xref table are computed here, not hand-typed,
    // so this is correct by construction rather than relying on manual arithmetic. Content
    // is plain ASCII throughout, so StringBuilder.Length (chars) equals byte count exactly.
    private static byte[] BuildMinimalPdf(string version = "1.7", int pageCount = 1)
    {
        var sb      = new StringBuilder();
        var offsets = new List<int>();

        void AppendObj(string content)
        {
            offsets.Add(sb.Length);
            sb.Append(content);
        }

        sb.Append($"%PDF-{version}\n");

        var kids = string.Join(" ", Enumerable.Range(0, pageCount).Select(i => $"{3 + i} 0 R"));
        AppendObj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        AppendObj($"2 0 obj\n<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>\nendobj\n");
        for (var i = 0; i < pageCount; i++)
            AppendObj($"{3 + i} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");

        var xrefOffset    = sb.Length;
        var totalObjects  = offsets.Count + 1; // +1 for the free entry (object 0)

        sb.Append($"xref\n0 {totalObjects}\n");
        sb.Append("0000000000 65535 f \n");
        foreach (var off in offsets)
            sb.Append($"{off:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {totalObjects} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    [TestMethod]
    public void EmptyFile_FailsWithEmptyFileReason()
    {
        var ok = PdfDocumentValidator.IsPDFValid([], "doc.pdf", NullLogger.Instance, out _, out var error, out var diagnostics);

        Assert.IsFalse(ok);
        Assert.AreEqual(PdfOpenFailureReason.EmptyFile, error!.Reason);
        Assert.AreEqual(1, diagnostics.Errors.Count);
        Assert.AreSame(error, diagnostics.Errors[0]);
    }

    [TestMethod]
    public void TooLargeFile_FailsWithTooLargeReason()
    {
        var bytes = new byte[PdfDocumentValidator.MaxBytes + 1];

        var ok = PdfDocumentValidator.IsPDFValid(bytes, "doc.pdf", NullLogger.Instance, out _, out var error, out var diagnostics);

        Assert.IsFalse(ok);
        Assert.AreEqual(PdfOpenFailureReason.TooLarge, error!.Reason);
        Assert.AreEqual(1, diagnostics.Errors.Count);
    }

    [TestMethod]
    public void NearLimitSizeFile_ProducesWarning_EvenThoughItFailsToOpen()
    {
        // IsPDFSizeOkForDI (and its near-limit warning) runs and completes before
        // TryOpenAndValidate is ever called, so the warning fires regardless of whether
        // these garbage bytes go on to open successfully - no need for a real ~400MB PDF.
        var bytes = new byte[(long)(PdfDocumentValidator.MaxBytes * 0.81)];

        var ok = PdfDocumentValidator.IsPDFValid(bytes, "doc.pdf", NullLogger.Instance, out _, out _, out var diagnostics);

        Assert.IsFalse(ok, "Garbage bytes shouldn't open as a real PDF.");
        Assert.IsTrue(diagnostics.Warnings.Any(w => w.Message.Contains("MB")),
            "The near-limit size warning should still have fired before the open attempt failed.");
    }

    [TestMethod]
    public void SmallFile_ProducesWarningButStillFailsToOpen()
    {
        // A handful of garbage bytes: small enough to trigger the "likely a placeholder"
        // warning, but not a real PDF, so it still fails at TryOpenAndValidate.
        var bytes = new byte[] { 1, 2, 3 };

        var ok = PdfDocumentValidator.IsPDFValid(bytes, "doc.pdf", NullLogger.Instance, out _, out var error, out var diagnostics);

        Assert.IsFalse(ok);
        Assert.IsTrue(diagnostics.Warnings.Any(w => w.Message.Contains("byte(s)")));
        Assert.AreEqual(PdfOpenFailureReason.MalformedFormat, error!.Reason);
    }

    [TestMethod]
    public void ValidMinimalPdf_Succeeds_NoErrors()
    {
        var bytes = BuildMinimalPdf(pageCount: 1);

        var ok = PdfDocumentValidator.IsPDFValid(bytes, "doc.pdf", NullLogger.Instance, out var pdf, out _, out var diagnostics);

        Assert.IsTrue(ok, "Minimal hand-built PDF should open via PdfPig.");
        Assert.AreEqual(1, pdf!.NumberOfPages);
        Assert.AreEqual(0, diagnostics.Errors.Count);
        pdf.Dispose();
    }

    [TestMethod]
    public void OldPdfVersion_ProducesVersionWarning()
    {
        var bytes = BuildMinimalPdf(version: "1.3", pageCount: 1);

        var ok = PdfDocumentValidator.IsPDFValid(bytes, "doc.pdf", NullLogger.Instance, out var pdf, out _, out var diagnostics);

        Assert.IsTrue(ok);
        Assert.IsTrue(diagnostics.Warnings.Any(w => w.Message.Contains("older than")));
        pdf!.Dispose();
    }

    [TestMethod]
    public void RecentPdfVersion_NoVersionWarning()
    {
        var bytes = BuildMinimalPdf(version: "1.7", pageCount: 1);

        var ok = PdfDocumentValidator.IsPDFValid(bytes, "doc.pdf", NullLogger.Instance, out var pdf, out _, out var diagnostics);

        Assert.IsTrue(ok);
        Assert.IsFalse(diagnostics.Warnings.Any(w => w.Message.Contains("older than")));
        pdf!.Dispose();
    }

    [TestMethod]
    public void ZeroPages_FailsWithEmptyDocumentReason()
    {
        var bytes = BuildMinimalPdf(pageCount: 0);

        var ok = PdfDocumentValidator.IsPDFValid(bytes, "doc.pdf", NullLogger.Instance, out _, out var error, out var diagnostics);

        Assert.IsFalse(ok);
        Assert.AreEqual(PdfOpenFailureReason.EmptyDocument, error!.Reason);
        Assert.AreEqual(1, diagnostics.Errors.Count);
    }

    [TestMethod]
    public void PageCountJustOverLimit_FailsWithTooManyPagesReason()
    {
        var bytes = BuildMinimalPdf(pageCount: PdfDocumentValidator.MaxPages + 1);

        var ok = PdfDocumentValidator.IsPDFValid(bytes, "doc.pdf", NullLogger.Instance, out _, out var error, out var diagnostics);

        Assert.IsFalse(ok);
        Assert.AreEqual(PdfOpenFailureReason.TooManyPages, error!.Reason);
        Assert.AreEqual(1, diagnostics.Errors.Count);
    }

    [TestMethod]
    public void PageCountNearLimit_ProducesWarningButSucceeds()
    {
        // 81% of MaxPages (2000) - past the 80% NearLimitFraction threshold, still under
        // the hard 2000-page cap.
        var nearLimitCount = (int)(PdfDocumentValidator.MaxPages * 0.81);
        var bytes = BuildMinimalPdf(pageCount: nearLimitCount);

        var ok = PdfDocumentValidator.IsPDFValid(bytes, "doc.pdf", NullLogger.Instance, out var pdf, out _, out var diagnostics);

        Assert.IsTrue(ok);
        Assert.IsTrue(diagnostics.Warnings.Any(w => w.Message.Contains("page(s)") || w.Message.Contains("pages")));
        pdf!.Dispose();
    }

    [TestMethod]
    public void PageCountWellUnderLimit_NoPageCountWarning()
    {
        // These hand-built PDFs are always well under MinReasonableBytes (10KB), so the
        // small-file warning always fires too - scope this assertion to the page-count
        // warning specifically rather than asserting the whole list is empty.
        var bytes = BuildMinimalPdf(pageCount: 5);

        var ok = PdfDocumentValidator.IsPDFValid(bytes, "doc.pdf", NullLogger.Instance, out var pdf, out _, out var diagnostics);

        Assert.IsTrue(ok);
        Assert.IsFalse(diagnostics.Warnings.Any(w => w.Message.Contains("Document Intelligence limit per analyze call")));
        pdf!.Dispose();
    }

    [TestMethod]
    public void TryOpenAndValidate_CalledDirectly_ValidPdf_ReturnsTrueAndOpensDocument()
    {
        var bytes    = BuildMinimalPdf(pageCount: 2);
        var warnings = new List<ExtractionWarning>();

        var ok = PdfDocumentValidator.TryOpenAndValidate(bytes, "doc.pdf", NullLogger.Instance, warnings, out var pdf, out var error);

        Assert.IsTrue(ok);
        Assert.IsNull(error);
        Assert.AreEqual(2, pdf!.NumberOfPages);
        pdf.Dispose();
    }

    [TestMethod]
    public void TryOpenAndValidate_CalledDirectly_MalformedBytes_ReturnsFalseWithMalformedReason()
    {
        var bytes    = Encoding.ASCII.GetBytes("this is not a pdf at all, just plain text padding to be a reasonable size 0123456789");
        var warnings = new List<ExtractionWarning>();

        var ok = PdfDocumentValidator.TryOpenAndValidate(bytes, "doc.pdf", NullLogger.Instance, warnings, out var pdf, out var error);

        Assert.IsFalse(ok);
        Assert.IsNull(pdf);
        Assert.IsNotNull(error);
    }

    [TestMethod]
    public void GarbageBytes_FailsToOpen_MirroredIntoDiagnosticsErrors()
    {
        var bytes = Encoding.ASCII.GetBytes("this is not a pdf at all, just plain text padding to be a reasonable size 0123456789");

        var ok = PdfDocumentValidator.IsPDFValid(bytes, "doc.pdf", NullLogger.Instance, out var pdf, out var error, out var diagnostics);

        Assert.IsFalse(ok);
        Assert.IsNull(pdf);
        Assert.IsNotNull(error);
        Assert.AreEqual(1, diagnostics.Errors.Count);
        Assert.AreSame(error, diagnostics.Errors[0]);
    }
}
