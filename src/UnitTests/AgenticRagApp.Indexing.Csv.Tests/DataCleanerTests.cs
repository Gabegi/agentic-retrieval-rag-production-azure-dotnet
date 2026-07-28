using Microsoft.VisualStudio.TestTools.UnitTesting;
using AgenticRagApp.Indexing.Csv.Models;
using AgenticRagApp.Indexing.Csv.Services;

namespace RagApp.UnitTests.CsvExtraction;

[TestClass]
public class DataCleanerTests
{
    private static DataCleaner BuildCleaner() => new();

    private static JoinedPageRecord Page(
        string docId       = "doc1",
        int    pageIndex   = 0,
        string content     = "Some content",
        string lastModified = "20240101120000",
        string checkDate   = "",
        string attentionFlags = "",
        string language    = "nl-NL") => new()
    {
        DocumentId        = docId,
        PageIndex         = pageIndex,
        PageContent       = content,
        LastModifiedRaw   = lastModified,
        CheckDateRaw      = checkDate,
        AttentionFlagsRaw = attentionFlags,
        Language          = language,
        Title             = " Title ",
        QuickCode         = " QC ",
        FolderPath        = " Folder ",
        RelativePath      = " rel ",
        DocumentTypeName  = " Protocol ",
        Summary           = " Summary ",
        Version           = " 7.0 ",
    };

    [TestMethod]
    public void ValidPage_IsCleanedAndTrimmed()
    {
        var result = BuildCleaner().Clean([Page()]);

        Assert.AreEqual(1, result.Records.Count);
        Assert.AreEqual(0, result.Errors.Count);
        var record = result.Records[0];
        Assert.AreEqual("Title", record.Title);
        Assert.AreEqual("QC", record.QuickCode);
        Assert.AreEqual("Protocol", record.DocumentTypeName);
        Assert.AreEqual(new DateTime(2024, 1, 1, 12, 0, 0), record.LastModified);
        Assert.IsNull(record.CheckDate);
    }

    [TestMethod]
    public void DuplicatePage_SameDocAndPageIndex_SecondIsSkipped()
    {
        var result = BuildCleaner().Clean([Page(pageIndex: 0), Page(pageIndex: 0)]);

        Assert.AreEqual(1, result.Records.Count);
        Assert.AreEqual(1, result.DuplicatePagesSkipped);
        Assert.AreEqual(1, result.Warnings.Count);
    }

    [TestMethod]
    public void SamePageIndexDifferentDocument_IsNotADuplicate()
    {
        var result = BuildCleaner().Clean([Page(docId: "doc1", pageIndex: 0), Page(docId: "doc2", pageIndex: 0)]);

        Assert.AreEqual(2, result.Records.Count);
        Assert.AreEqual(0, result.DuplicatePagesSkipped);
    }

    [TestMethod]
    public void InvalidLastModifiedDate_IsError()
    {
        var result = BuildCleaner().Clean([Page(lastModified: "not-a-date")]);

        Assert.AreEqual(0, result.Records.Count);
        Assert.AreEqual(1, result.Errors.Count);
        StringAssert.Contains(result.Errors[0].Message, "LastModifiedRaw");
    }

    [TestMethod]
    public void EmptyCheckDate_ParsesAsNull()
    {
        var result = BuildCleaner().Clean([Page(checkDate: "")]);

        Assert.IsNull(result.Records[0].CheckDate);
        Assert.AreEqual(0, result.Errors.Count);
    }

    [TestMethod]
    public void ValidCheckDate_IsParsed()
    {
        var result = BuildCleaner().Clean([Page(checkDate: "20240615")]);

        Assert.AreEqual(new DateTime(2024, 6, 15), result.Records[0].CheckDate);
    }

    [TestMethod]
    public void InvalidCheckDate_IsError()
    {
        var result = BuildCleaner().Clean([Page(checkDate: "not-a-date")]);

        Assert.AreEqual(0, result.Records.Count);
        Assert.AreEqual(1, result.Errors.Count);
        StringAssert.Contains(result.Errors[0].Message, "CheckDateRaw");
    }

    [TestMethod]
    public void EmptyAttentionFlags_ParsesAsEmptyList()
    {
        var result = BuildCleaner().Clean([Page(attentionFlags: "")]);

        Assert.AreEqual(0, result.Records[0].AttentionFlags.Count);
    }

    [TestMethod]
    public void ValidAttentionFlagsJson_IsParsed()
    {
        var result = BuildCleaner().Clean([Page(attentionFlags: "[\"check_date_exceeded\"]")]);

        CollectionAssert.Contains(result.Records[0].AttentionFlags, "check_date_exceeded");
    }

    [TestMethod]
    public void InvalidAttentionFlagsJson_IsError()
    {
        var result = BuildCleaner().Clean([Page(attentionFlags: "not-json")]);

        Assert.AreEqual(0, result.Records.Count);
        Assert.AreEqual(1, result.Errors.Count);
        StringAssert.Contains(result.Errors[0].Message, "AttentionFlagsRaw");
    }

    [TestMethod]
    public void EmptyContentAfterCleanup_ProducesWarningNotError()
    {
        var result = BuildCleaner().Clean([Page(content: "   ")]);

        Assert.AreEqual(1, result.Records.Count);
        Assert.AreEqual(1, result.Warnings.Count);
        Assert.AreEqual(0, result.Errors.Count);
    }

    [TestMethod]
    public void CordaanLogoLine_IsStrippedFromContent()
    {
        var result = BuildCleaner().Clean([Page(content: "Intro text\ncordaan\nMore text")]);

        StringAssert.DoesNotMatch(result.Records[0].PageContent, new System.Text.RegularExpressions.Regex("^cordaan$", System.Text.RegularExpressions.RegexOptions.Multiline));
        StringAssert.Contains(result.Records[0].PageContent, "Intro text");
        StringAssert.Contains(result.Records[0].PageContent, "More text");
    }

    [TestMethod]
    public void CordaanWordWithinProse_IsNotStripped()
    {
        var result = BuildCleaner().Clean([Page(content: "Welcome to Cordaan, our organisation.")]);

        StringAssert.Contains(result.Records[0].PageContent, "Cordaan");
    }

    [TestMethod]
    public void ImagePlaceholder_IsRemoved()
    {
        var result = BuildCleaner().Clean([Page(content: "Before ![alt](path/to/image.png) After")]);

        StringAssert.DoesNotMatch(result.Records[0].PageContent, new System.Text.RegularExpressions.Regex(@"!\["));
        StringAssert.Contains(result.Records[0].PageContent, "Before");
        StringAssert.Contains(result.Records[0].PageContent, "After");
    }

    [TestMethod]
    public void ExcessBlankLines_AreCollapsed()
    {
        var result = BuildCleaner().Clean([Page(content: "Line one\n\n\n\n\nLine two")]);

        Assert.AreEqual("Line one\n\nLine two", result.Records[0].PageContent);
    }

    [TestMethod]
    public void HtmlEntities_AreDecoded()
    {
        var result = BuildCleaner().Clean([Page(content: "Tom &amp; Jerry &lt;3")]);

        Assert.AreEqual("Tom & Jerry <3", result.Records[0].PageContent);
    }

    [TestMethod]
    public void OneBadPageAmongGoodPages_OnlyBadPageFails()
    {
        var pages = new[]
        {
            Page(docId: "doc1", pageIndex: 0),
            Page(docId: "doc2", pageIndex: 0, lastModified: "garbage"),
            Page(docId: "doc3", pageIndex: 0),
        };

        var result = BuildCleaner().Clean(pages);

        Assert.AreEqual(2, result.Records.Count);
        Assert.AreEqual(1, result.Errors.Count);
    }
}
