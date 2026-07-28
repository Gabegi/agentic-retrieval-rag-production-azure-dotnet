using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using AgenticRagApp.Indexing.Pdf.Services;
using UglyToad.PdfPig;

namespace RagApp.UnitTests.PdfExtraction;

[TestClass]
public class PdfNativeMetadataExtractorTests
{
    // Builds a minimal, real, PdfPig-openable PDF with a controllable Info dictionary and
    // an optional single-item outline/bookmark - byte offsets computed here, not hand-typed
    // (same approach as PdfDocumentValidatorTests, for the same reason: correctness by
    // construction rather than manual arithmetic).
    private static PdfDocument OpenPdf(
        string? title = null, string? author = null, string? creationDate = null, bool withBookmark = false,
        string? modDate = null, string? producer = null, string? creator = null, string? subject = null, string? keywords = null)
    {
        var sb      = new StringBuilder();
        var offsets = new List<int>();

        void AppendObj(string content)
        {
            offsets.Add(sb.Length);
            sb.Append(content);
        }

        sb.Append("%PDF-1.7\n");

        var outlinesRef = withBookmark ? " /Outlines 4 0 R" : "";
        AppendObj($"1 0 obj\n<< /Type /Catalog /Pages 2 0 R{outlinesRef} >>\nendobj\n");
        AppendObj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        AppendObj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");

        if (withBookmark)
        {
            AppendObj("4 0 obj\n<< /Type /Outlines /First 5 0 R /Last 5 0 R /Count 1 >>\nendobj\n");
            AppendObj("5 0 obj\n<< /Title (Chapter 1) /Parent 4 0 R /Dest [3 0 R /Fit] >>\nendobj\n");
        }

        var infoParts = new List<string>();
        if (title is not null)        infoParts.Add($"/Title ({EscapePdfString(title)})");
        if (author is not null)       infoParts.Add($"/Author ({EscapePdfString(author)})");
        if (creationDate is not null) infoParts.Add($"/CreationDate ({creationDate})");
        if (modDate is not null)      infoParts.Add($"/ModDate ({modDate})");
        if (producer is not null)     infoParts.Add($"/Producer ({EscapePdfString(producer)})");
        if (creator is not null)      infoParts.Add($"/Creator ({EscapePdfString(creator)})");
        if (subject is not null)      infoParts.Add($"/Subject ({EscapePdfString(subject)})");
        if (keywords is not null)     infoParts.Add($"/Keywords ({EscapePdfString(keywords)})");

        var hasInfo   = infoParts.Count > 0;
        var infoObjId = withBookmark ? 6 : 4;
        if (hasInfo)
            AppendObj($"{infoObjId} 0 obj\n<< {string.Join(" ", infoParts)} >>\nendobj\n");

        var xrefOffset      = sb.Length;
        var totalObjects    = offsets.Count + 1;
        sb.Append($"xref\n0 {totalObjects}\n");
        sb.Append("0000000000 65535 f \n");
        foreach (var off in offsets)
            sb.Append($"{off:D10} 00000 n \n");

        var infoTrailerRef = hasInfo ? $" /Info {infoObjId} 0 R" : "";
        sb.Append($"trailer\n<< /Size {totalObjects} /Root 1 0 R{infoTrailerRef} >>\nstartxref\n{xrefOffset}\n%%EOF");

        return PdfDocument.Open(Encoding.ASCII.GetBytes(sb.ToString()));
    }

    private static string EscapePdfString(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    [TestMethod]
    public void NoTitle_ProducesWarning()
    {
        var metadata = PdfNativeMetadataExtractor.ExtractPdfNativeMetadataAndDispose(OpenPdf(), "doc.pdf", NullLogger.Instance, out var diagnostics);

        Assert.IsNull(metadata.Title);
        Assert.IsTrue(diagnostics.Warnings.Any(w => w.Message.Contains("No native Title")));
    }

    [TestMethod]
    public void HasTitle_NoTitleWarning()
    {
        var metadata = PdfNativeMetadataExtractor.ExtractPdfNativeMetadataAndDispose(OpenPdf(title: "My Document"), "doc.pdf", NullLogger.Instance, out var diagnostics);

        Assert.AreEqual("My Document", metadata.Title);
        Assert.IsFalse(diagnostics.Warnings.Any(w => w.Message.Contains("No native Title")));
    }

    [TestMethod]
    public void NoAuthor_ProducesWarning()
    {
        var metadata = PdfNativeMetadataExtractor.ExtractPdfNativeMetadataAndDispose(OpenPdf(), "doc.pdf", NullLogger.Instance, out var diagnostics);

        Assert.IsNull(metadata.Author);
        Assert.IsTrue(diagnostics.Warnings.Any(w => w.Message.Contains("No native Author")));
    }

    [TestMethod]
    public void HasAuthor_NoAuthorWarning()
    {
        var metadata = PdfNativeMetadataExtractor.ExtractPdfNativeMetadataAndDispose(OpenPdf(author: "Jane Doe"), "doc.pdf", NullLogger.Instance, out var diagnostics);

        Assert.AreEqual("Jane Doe", metadata.Author);
        Assert.IsFalse(diagnostics.Warnings.Any(w => w.Message.Contains("No native Author")));
    }

    [TestMethod]
    public void NoCreationDate_ProducesWarning()
    {
        var metadata = PdfNativeMetadataExtractor.ExtractPdfNativeMetadataAndDispose(OpenPdf(), "doc.pdf", NullLogger.Instance, out var diagnostics);

        Assert.IsNull(metadata.CreatedAt);
        Assert.IsTrue(diagnostics.Warnings.Any(w => w.Message.Contains("No native CreationDate")));
    }

    [TestMethod]
    public void ValidCreationDate_IsParsed_NoWarning()
    {
        var metadata = PdfNativeMetadataExtractor.ExtractPdfNativeMetadataAndDispose(
            OpenPdf(creationDate: "D:20200115093000"), "doc.pdf", NullLogger.Instance, out var diagnostics);

        Assert.AreEqual(new DateTimeOffset(2020, 1, 15, 9, 30, 0, TimeSpan.Zero), metadata.CreatedAt);
        Assert.IsFalse(diagnostics.Warnings.Any(w => w.Message.Contains("could not be parsed")));
        Assert.IsFalse(diagnostics.Warnings.Any(w => w.Message.Contains("in the future")));
    }

    [TestMethod]
    public void UnparseableCreationDate_ProducesWarningWithRawValue()
    {
        var metadata = PdfNativeMetadataExtractor.ExtractPdfNativeMetadataAndDispose(
            OpenPdf(creationDate: "not-a-date"), "doc.pdf", NullLogger.Instance, out var diagnostics);

        Assert.IsNull(metadata.CreatedAt);
        Assert.IsTrue(diagnostics.Warnings.Any(w => w.Message.Contains("could not be parsed") && w.Message.Contains("not-a-date")));
    }

    [TestMethod]
    public void FutureCreationDate_ProducesFutureWarning()
    {
        var futureYear = DateTimeOffset.UtcNow.Year + 5;
        var metadata = PdfNativeMetadataExtractor.ExtractPdfNativeMetadataAndDispose(
            OpenPdf(creationDate: $"D:{futureYear}0101120000"), "doc.pdf", NullLogger.Instance, out var diagnostics);

        Assert.IsNotNull(metadata.CreatedAt);
        Assert.IsTrue(diagnostics.Warnings.Any(w => w.Message.Contains("is in the future")));
    }

    [TestMethod]
    public void NoBookmarks_ProducesInfo()
    {
        var metadata = PdfNativeMetadataExtractor.ExtractPdfNativeMetadataAndDispose(OpenPdf(), "doc.pdf", NullLogger.Instance, out var diagnostics);

        Assert.AreEqual(0, metadata.Bookmarks!.Count);
        Assert.IsTrue(diagnostics.Info.Any(w => w.Message.Contains("No bookmarks/outline present")));
    }

    [TestMethod]
    public void HasBookmark_ProducesCountAndDepthInfo()
    {
        var metadata = PdfNativeMetadataExtractor.ExtractPdfNativeMetadataAndDispose(
            OpenPdf(withBookmark: true), "doc.pdf", NullLogger.Instance, out var diagnostics);

        Assert.AreEqual(1, metadata.Bookmarks!.Count);
        Assert.IsTrue(diagnostics.Info.Any(w => w.Message.Contains("1 bookmark(s) found")));
    }

    [TestMethod]
    public void NoProducer_ProducesWarning()
    {
        var metadata = PdfNativeMetadataExtractor.ExtractPdfNativeMetadataAndDispose(OpenPdf(), "doc.pdf", NullLogger.Instance, out var diagnostics);

        Assert.IsNull(metadata.Producer);
        Assert.IsTrue(diagnostics.Warnings.Any(w => w.Message.Contains("No native Producer")));
    }

    [TestMethod]
    public void HasProducer_NoProducerWarning()
    {
        var metadata = PdfNativeMetadataExtractor.ExtractPdfNativeMetadataAndDispose(
            OpenPdf(producer: "Microsoft Word"), "doc.pdf", NullLogger.Instance, out var diagnostics);

        Assert.AreEqual("Microsoft Word", metadata.Producer);
        Assert.IsFalse(diagnostics.Warnings.Any(w => w.Message.Contains("No native Producer")));
    }

    [TestMethod]
    public void ValidModDate_IsParsed_NoWarning()
    {
        var metadata = PdfNativeMetadataExtractor.ExtractPdfNativeMetadataAndDispose(
            OpenPdf(modDate: "D:20200115093000"), "doc.pdf", NullLogger.Instance, out var diagnostics);

        Assert.AreEqual(new DateTimeOffset(2020, 1, 15, 9, 30, 0, TimeSpan.Zero), metadata.ModDate);
        Assert.IsFalse(diagnostics.Warnings.Any(w => w.Message.Contains("ModDate") && w.Message.Contains("could not be parsed")));
    }

    [TestMethod]
    public void UnparseableModDate_ProducesWarningWithRawValue()
    {
        var metadata = PdfNativeMetadataExtractor.ExtractPdfNativeMetadataAndDispose(
            OpenPdf(modDate: "not-a-date"), "doc.pdf", NullLogger.Instance, out var diagnostics);

        Assert.IsNull(metadata.ModDate);
        Assert.IsTrue(diagnostics.Warnings.Any(w => w.Message.Contains("ModDate") && w.Message.Contains("could not be parsed") && w.Message.Contains("not-a-date")));
    }

    [TestMethod]
    public void SubjectAndKeywords_ReadWhenPresent_NullWhenAbsent()
    {
        var absent = PdfNativeMetadataExtractor.ExtractPdfNativeMetadataAndDispose(OpenPdf(), "doc.pdf", NullLogger.Instance, out _);
        Assert.IsNull(absent.Subject);
        Assert.IsNull(absent.Keywords);
        Assert.IsNull(absent.Creator);

        var present = PdfNativeMetadataExtractor.ExtractPdfNativeMetadataAndDispose(
            OpenPdf(subject: "HR Policy", keywords: "gedragscode, hr", creator: "Microsoft Word"), "doc.pdf", NullLogger.Instance, out _);
        Assert.AreEqual("HR Policy", present.Subject);
        Assert.AreEqual("gedragscode, hr", present.Keywords);
        Assert.AreEqual("Microsoft Word", present.Creator);
    }
}
