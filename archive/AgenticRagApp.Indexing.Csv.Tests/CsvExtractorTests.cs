using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using AgenticRagApp.Indexing.Csv.Models;
using AgenticRagApp.Indexing.Csv.Services;

namespace RagApp.UnitTests.CsvExtraction;

[TestClass]
public class CsvExtractorTests
{
    private static CsvExtractor BuildExtractor() => new(NullLogger<CsvExtractor>.Instance);

    private static Stream ToStream(string csv) => new MemoryStream(Encoding.UTF8.GetBytes(csv));

    private const string PagesHeader =
        "DOCUMENT_ID,TITLE,QUICK_CODE,FOLDER_MINI_FULL_PATH,LAST_MODIFIED_DATETIME,PAGE_INDEX,PAGE_CONTENT,RELATIVE_PATH,LANGUAGE";

    private const string IndexHeader =
        "DOCUMENT_ID,DOCUMENT_TYPE_NAME,SUMMARY,VERSION,REVISION,CHECK_DATE,ATTENTION_REQUIRED_FLAGS,ACTIVE";

    [TestMethod]
    public void ExtractPages_ValidRow_ProducesRecord()
    {
        var csv = PagesHeader + "\n" +
                  "doc1,Title,QC1,Folder/Path,20240101120000,0,Some content,rel/path,nl-NL\n";

        var result = BuildExtractor().ExtractPages(ToStream(csv));

        Assert.AreEqual(1, result.Records.Count);
        Assert.AreEqual(0, result.Errors.Count);
        var record = result.Records[0];
        Assert.AreEqual("doc1", record.DocumentId);
        Assert.AreEqual("Title", record.Title);
        Assert.AreEqual(0, record.PageIndex);
        Assert.AreEqual("Some content", record.PageContent);
        Assert.AreEqual("nl-NL", record.Language);
    }

    [TestMethod]
    public void ExtractPages_LogsDetectedEncoding()
    {
        // No BOM in ToStream's raw UTF-8 bytes, so StreamReader falls back to the UTF-8
        // default it was explicitly constructed with (EnsureHeadersAreCorrect) - this
        // verifies that fallback is actually observable, not just assumed.
        var logger    = new Mock<ILogger<CsvExtractor>>();
        var extractor = new CsvExtractor(logger.Object);
        var csv = PagesHeader + "\n" +
                  "doc1,Title,QC1,Folder/Path,20240101120000,0,Some content,rel/path,nl-NL\n";

        extractor.ExtractPages(ToStream(csv));

        logger.Verify(l => l.Log(
            LogLevel.Information,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("utf-8", StringComparison.OrdinalIgnoreCase)),
            null,
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [TestMethod]
    public void ExtractPages_SemicolonDelimitedFile_ThrowsSpecificDelimiterError()
    {
        // Same header/data, but delimited with ';' instead of our hardcoded ',' - CsvHelper
        // reads the entire header line as one field since there's nothing to split it on.
        var csv = PagesHeader.Replace(',', ';') + "\n" +
                  "doc1;Title;QC1;Folder/Path;20240101120000;0;Some content;rel/path;nl-NL\n";

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => BuildExtractor().ExtractPages(ToStream(csv)));
        StringAssert.Contains(ex.Message, "delimiter");
    }

    [TestMethod]
    public void ExtractPages_MissingRequiredHeader_Throws()
    {
        var csv = "DOCUMENT_ID,TITLE\ndoc1,Title\n";

        Assert.ThrowsExactly<InvalidOperationException>(() => BuildExtractor().ExtractPages(ToStream(csv)));
    }

    [TestMethod]
    public void ExtractPages_HeaderCasingDiffers_StillMatches()
    {
        var lowerHeader = PagesHeader.ToLowerInvariant();
        var csv = lowerHeader + "\n" +
                  "doc1,Title,QC1,Folder/Path,20240101120000,0,Some content,rel/path,nl-NL\n";

        var result = BuildExtractor().ExtractPages(ToStream(csv));

        Assert.AreEqual(1, result.Records.Count);
        Assert.AreEqual(0, result.Errors.Count);
    }

    [TestMethod]
    public void ExtractPages_BlankDocumentId_IsRowError()
    {
        var csv = PagesHeader + "\n" +
                  ",Title,QC1,Folder/Path,20240101120000,0,Some content,rel/path,nl-NL\n";

        var result = BuildExtractor().ExtractPages(ToStream(csv));

        Assert.AreEqual(0, result.Records.Count);
        Assert.AreEqual(1, result.Errors.Count);
        StringAssert.Contains(result.Errors[0].Message, "DOCUMENT_ID");
    }

    [TestMethod]
    public void ExtractPages_DocumentIdWithWhitespace_IsTrimmed()
    {
        var csv = PagesHeader + "\n" +
                  " doc1 ,Title,QC1,Folder/Path,20240101120000,0,Some content,rel/path,nl-NL\n";

        var result = BuildExtractor().ExtractPages(ToStream(csv));

        Assert.AreEqual(1, result.Records.Count);
        Assert.AreEqual("doc1", result.Records[0].DocumentId);
    }

    [TestMethod]
    public void ExtractPages_NonNumericPageIndex_IsRowError()
    {
        var csv = PagesHeader + "\n" +
                  "doc1,Title,QC1,Folder/Path,20240101120000,not-a-number,Some content,rel/path,nl-NL\n";

        var result = BuildExtractor().ExtractPages(ToStream(csv));

        Assert.AreEqual(0, result.Records.Count);
        Assert.AreEqual(1, result.Errors.Count);
        StringAssert.Contains(result.Errors[0].Message, "PAGE_INDEX");
    }

    [TestMethod]
    public void ExtractPages_OneBadRowAmongGoodRows_OnlyBadRowFails()
    {
        var csv = PagesHeader + "\n" +
                  "doc1,Title,QC1,Folder/Path,20240101120000,0,Content one,rel/path,nl-NL\n" +
                  ",Title,QC1,Folder/Path,20240101120000,1,Content two,rel/path,nl-NL\n" +
                  "doc3,Title,QC1,Folder/Path,20240101120000,2,Content three,rel/path,nl-NL\n";

        var result = BuildExtractor().ExtractPages(ToStream(csv));

        Assert.AreEqual(2, result.Records.Count);
        Assert.AreEqual(1, result.Errors.Count);
        Assert.AreEqual(3, result.RowsAttempted);
    }

    [TestMethod]
    public void ExtractIndex_ValidRow_ProducesRecord()
    {
        var csv = IndexHeader + "\n" +
                  "doc1,Protocol,A summary,7,0,20240101,[],true\n";

        var result = BuildExtractor().ExtractIndex(ToStream(csv));

        Assert.AreEqual(1, result.Records.Count);
        var record = result.Records[0];
        Assert.AreEqual("doc1", record.DocumentId);
        Assert.AreEqual("Protocol", record.DocumentTypeName);
        Assert.AreEqual("7.0", record.Version);
        Assert.IsTrue(record.Active);
    }

    [TestMethod]
    public void ExtractIndex_MissingRevision_FallsBackToBareVersion()
    {
        var csv = IndexHeader + "\n" +
                  "doc1,Protocol,A summary,7,,20240101,[],true\n";

        var result = BuildExtractor().ExtractIndex(ToStream(csv));

        Assert.AreEqual("7", result.Records[0].Version);
    }

    [TestMethod]
    public void ExtractIndex_MissingActiveColumn_DefaultsToActive()
    {
        var csv = "DOCUMENT_ID,DOCUMENT_TYPE_NAME,SUMMARY,VERSION,REVISION,CHECK_DATE,ATTENTION_REQUIRED_FLAGS\n" +
                  "doc1,Protocol,A summary,7,0,20240101,[]\n";

        var result = BuildExtractor().ExtractIndex(ToStream(csv));

        Assert.AreEqual(1, result.Records.Count);
        Assert.IsTrue(result.Records[0].Active);
    }

    [TestMethod]
    public void ExtractIndex_ActiveFalse_IsParsed()
    {
        var csv = IndexHeader + "\n" +
                  "doc1,Protocol,A summary,7,0,20240101,[],false\n";

        var result = BuildExtractor().ExtractIndex(ToStream(csv));

        Assert.IsFalse(result.Records[0].Active);
    }

    [TestMethod]
    public void ExtractIndex_UnparseableActiveValue_IsRowError()
    {
        var csv = IndexHeader + "\n" +
                  "doc1,Protocol,A summary,7,0,20240101,[],maybe\n";

        var result = BuildExtractor().ExtractIndex(ToStream(csv));

        Assert.AreEqual(0, result.Records.Count);
        Assert.AreEqual(1, result.Errors.Count);
        StringAssert.Contains(result.Errors[0].Message, "ACTIVE");
    }
}
