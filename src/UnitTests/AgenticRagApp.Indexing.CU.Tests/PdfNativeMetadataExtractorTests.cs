using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using AgenticRagApp.Indexing.CU.Services;
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
    public void NoAuthor_ProducesInfoNotWarning()
    {
        // Finding #15: Author has no downstream consequence (unlike Title/Producer), so
        // it's reported as Info, not a Warning that would compete with real defects for
        // the validation report's truncated Issues budget.
        var metadata = PdfNativeMetadataExtractor.ExtractPdfNativeMetadataAndDispose(OpenPdf(), "doc.pdf", NullLogger.Instance, out var diagnostics);

        Assert.IsNull(metadata.Author);
        Assert.IsFalse(diagnostics.Warnings.Any(w => w.Message.Contains("No native Author")));
        Assert.IsTrue(diagnostics.Info.Any(i => i.Message.Contains("No native Author")));
    }

    [TestMethod]
    public void HasAuthor_NoAuthorInfoOrWarning()
    {
        var metadata = PdfNativeMetadataExtractor.ExtractPdfNativeMetadataAndDispose(OpenPdf(author: "Jane Doe"), "doc.pdf", NullLogger.Instance, out var diagnostics);

        Assert.AreEqual("Jane Doe", metadata.Author);
        Assert.IsFalse(diagnostics.Warnings.Any(w => w.Message.Contains("No native Author")));
        Assert.IsFalse(diagnostics.Info.Any(i => i.Message.Contains("No native Author")));
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

    [TestMethod]
    public void PageDimensions_ReadFromMediaBox_InPoints()
    {
        // OpenPdf's single page hardcodes /MediaBox [0 0 612 792] - US Letter in points
        // (612/72 = 8.5in, 792/72 = 11in). Unit is "point" here, never "inch": the
        // inch conversion happens later, in PageDimensionWarningsHelper.
        var metadata = PdfNativeMetadataExtractor.ExtractPdfNativeMetadataAndDispose(OpenPdf(), "doc.pdf", NullLogger.Instance, out _);

        Assert.AreEqual(1, metadata.NativePageDimensions!.Count);
        var page = metadata.NativePageDimensions[0];
        Assert.AreEqual(1, page.PageNumber);
        Assert.AreEqual(612.0, page.Width);
        Assert.AreEqual(792.0, page.Height);
        Assert.AreEqual("point", page.Unit);
    }

    // --- AltContainerValue --------------------------------------------------------------
    // dc:title is an rdf:Alt (language alternatives of one value) - AltContainerValue picks
    // the x-default item, else the first entry, else falls back to the element's own bare
    // text for a tool that skips the container entirely. Plain XElement fragments, no PDF
    // fixture needed - this method never touches PdfPig at all.

    private const string RdfNs = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";

    [TestMethod]
    public void AltContainer_WithXDefaultAmongOthers_PicksXDefaultItem()
    {
        var element = XElement.Parse($"""
            <title xmlns:rdf="{RdfNs}" xmlns:xml="http://www.w3.org/XML/1998/namespace">
              <rdf:Alt>
                <rdf:li xml:lang="en">English Title</rdf:li>
                <rdf:li xml:lang="x-default">Default Title</rdf:li>
                <rdf:li xml:lang="fr">French Title</rdf:li>
              </rdf:Alt>
            </title>
            """);

        var value = PdfNativeMetadataExtractor.AltContainerValue(element);

        Assert.AreEqual("Default Title", value);
    }

    [TestMethod]
    public void AltContainer_WithNoXDefault_PicksFirstItem()
    {
        var element = XElement.Parse($"""
            <title xmlns:rdf="{RdfNs}" xmlns:xml="http://www.w3.org/XML/1998/namespace">
              <rdf:Alt>
                <rdf:li xml:lang="en">First Title</rdf:li>
                <rdf:li xml:lang="fr">Second Title</rdf:li>
              </rdf:Alt>
            </title>
            """);

        var value = PdfNativeMetadataExtractor.AltContainerValue(element);

        Assert.AreEqual("First Title", value);
    }

    [TestMethod]
    public void AltContainer_SingleItemNoLangAttribute_IsUsed()
    {
        var element = XElement.Parse($"""
            <title xmlns:rdf="{RdfNs}">
              <rdf:Alt>
                <rdf:li>Only Title</rdf:li>
              </rdf:Alt>
            </title>
            """);

        var value = PdfNativeMetadataExtractor.AltContainerValue(element);

        Assert.AreEqual("Only Title", value);
    }

    [TestMethod]
    public void AltContainer_EmptyRdfAlt_ReturnsNull()
    {
        var element = XElement.Parse($"""
            <title xmlns:rdf="{RdfNs}">
              <rdf:Alt />
            </title>
            """);

        var value = PdfNativeMetadataExtractor.AltContainerValue(element);

        Assert.IsNull(value);
    }

    [TestMethod]
    public void BareStringElement_NoContainerAtAll_FallsBackToElementValue()
    {
        // A tool that writes dc:title as a plain string instead of an rdf:Alt container -
        // ContainerItems finds no rdf:li descendants at all, so this falls back to the
        // element's own text.
        var element = XElement.Parse("<title>Bare String Title</title>");

        var value = PdfNativeMetadataExtractor.AltContainerValue(element);

        Assert.AreEqual("Bare String Title", value);
    }

    [TestMethod]
    public void BareStringElement_WhitespaceOnly_ReturnsNull()
    {
        var element = XElement.Parse("<title>   </title>");

        var value = PdfNativeMetadataExtractor.AltContainerValue(element);

        Assert.IsNull(value);
    }

    [TestMethod]
    public void NullElement_ReturnsNull()
    {
        Assert.IsNull(PdfNativeMetadataExtractor.AltContainerValue(null));
    }
}
