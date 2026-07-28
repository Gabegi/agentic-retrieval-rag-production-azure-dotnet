using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using AgenticRagApp.Indexing.Csv.Models;
using AgenticRagApp.Indexing.Csv.Services;
using AgenticRagApp.Common.Models;

namespace RagApp.UnitTests.CsvExtraction;

[TestClass]
public class PipelineValidatorTests
{
    private static CsvExtractor      BuildExtractor() => new(NullLogger<CsvExtractor>.Instance);
    private static CsvJoiner         BuildJoiner()    => new();
    private static DataCleaner       BuildCleaner()   => new();
    private static PipelineValidator BuildValidator() => new();

    // Full header set including the optional-but-not-TryGetField columns (LANGUAGE,
    // REVISION) that CsvExtractor reads with GetField rather than TryGetField - omitting
    // them entirely (as opposed to leaving the value blank) throws MissingFieldException.
    private const string PagesHeader =
        "DOCUMENT_ID,TITLE,QUICK_CODE,FOLDER_MINI_FULL_PATH,LAST_MODIFIED_DATETIME,PAGE_INDEX,PAGE_CONTENT,RELATIVE_PATH,LANGUAGE";
    private const string IndexHeader =
        "DOCUMENT_ID,DOCUMENT_TYPE_NAME,SUMMARY,VERSION,REVISION,CHECK_DATE,ATTENTION_REQUIRED_FLAGS";

    // Builds a clean, all-good pipeline: one page, joined, cleaned, no errors anywhere -
    // the baseline every test below perturbs one piece of.
    private static (ExtractionResult<PageRecord> Pages, ExtractionResult<IndexRecord> Index, JoinResult Join, CleanResult Clean) HappyPath()
    {
        var pages = BuildExtractor().ExtractPages(ToStream(
            PagesHeader + "\n" +
            "doc1,Title,QC,Folder,20240101120000,0,Some markdown content,rel,nl-NL\n"));
        var index = BuildExtractor().ExtractIndex(ToStream(
            IndexHeader + "\n" +
            "doc1,Protocol,Summary,7,0,,[]\n"));
        var join  = BuildJoiner().Join(pages.Records, index.Records);
        var clean = BuildCleaner().Clean(join.Joined);
        return (pages, index, join, clean);
    }

    private static Stream ToStream(string csv) => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csv));

    // Empty extraction results still need Records/Errors/Warnings set — ExtractionResult<T>
    // has required init-only members, so a parameterless `new()` no longer compiles.
    private static ExtractionResult<T> Empty<T>() => new() { Records = [], Errors = [], Warnings = [] };

    // JoinedPageRecord builder for PipelineValidator tests that skip the join step -
    // LastModifiedRaw must be a valid "yyyyMMddHHmmss" value or DataCleaner rejects the
    // whole record with a CleaningError before PipelineValidator ever sees it.
    private static JoinedPageRecord JoinedPage(
        string docId, string content, string language = "nl-NL", string attentionFlagsRaw = "") => new()
    {
        DocumentId        = docId,
        PageIndex         = 0,
        PageContent       = content,
        LastModifiedRaw   = "20240101120000",
        Language          = language,
        AttentionFlagsRaw = attentionFlagsRaw,
    };

    [TestMethod]
    public void HappyPath_Passes()
    {
        var (pages, index, join, clean) = HappyPath();

        var report = BuildValidator().Validate(pages, index, join, clean);

        Assert.IsTrue(report.Passed);
        Assert.AreEqual(0, report.ReconciliationProblems.Count);
    }

    [TestMethod]
    public void NoInputAtAll_FailsRatherThanPassingVacuously()
    {
        var pages = Empty<PageRecord>();
        var index = Empty<IndexRecord>();
        var join  = BuildJoiner().Join(pages.Records, index.Records);
        var clean = BuildCleaner().Clean(join.Joined);

        var report = BuildValidator().Validate(pages, index, join, clean);

        // totalAttempted == 0 forces errorRate to 100, so an empty run never passes silently.
        Assert.IsFalse(report.Passed);
    }

    [TestMethod]
    public void JoinError_IsSurfacedAsIssueAndCountsTowardErrorRate()
    {
        var pages = BuildExtractor().ExtractPages(ToStream(
            PagesHeader + "\n" +
            "doc1,Title,QC,Folder,20240101120000,0,Content,rel,nl-NL\n"));
        var index = Empty<IndexRecord>(); // no matching index record for doc1
        var join  = BuildJoiner().Join(pages.Records, index.Records);
        var clean = BuildCleaner().Clean(join.Joined);

        var report = BuildValidator().Validate(pages, index, join, clean);

        Assert.IsTrue(report.Issues.Any(i => i.Stage == "Join" && i.Severity == "Error"));
    }

    [TestMethod]
    public void SkippedIndexRecord_ProducesWarningAndRedFlag()
    {
        var pages = Empty<PageRecord>();
        var index = BuildExtractor().ExtractIndex(ToStream(
            IndexHeader + "\n" +
            "doc1,Protocol,Summary,7,0,,[]\n"));
        var join  = BuildJoiner().Join(pages.Records, index.Records);
        var clean = BuildCleaner().Clean(join.Joined);

        var report = BuildValidator().Validate(pages, index, join, clean);

        Assert.AreEqual(1, report.SkippedIndexDocuments.Count);
        Assert.IsTrue(report.RedFlags.Any(f => f.Contains("no pages")));
    }

    [TestMethod]
    public void StaleDocument_IsFlaggedAndCounted()
    {
        var flaggedJoin = new List<JoinedPageRecord>
        {
            JoinedPage("doc1", "content", attentionFlagsRaw: "[\"check_date_exceeded\"]"),
        };
        var clean = BuildCleaner().Clean(flaggedJoin);

        var pages = Empty<PageRecord>();
        var index = Empty<IndexRecord>();
        var join  = new JoinResult();

        var report = BuildValidator().Validate(pages, index, join, clean);

        Assert.AreEqual(1, report.StaleDocCount);
        Assert.IsTrue(report.RedFlags.Any(f => f.Contains("check_date_exceeded")));
    }

    [TestMethod]
    public void ContentWithoutHeadings_NeedsFallbackChunking()
    {
        var joined = new List<JoinedPageRecord> { JoinedPage("doc1", "Plain text, no headings at all.") };
        var clean = BuildCleaner().Clean(joined);
        var pages = Empty<PageRecord>();
        var index = Empty<IndexRecord>();
        var join  = new JoinResult();

        var report = BuildValidator().Validate(pages, index, join, clean);

        CollectionAssert.Contains(report.DocumentsNeedingFallbackChunking.ToList(), "doc1");
    }

    [TestMethod]
    public void ContentWithMarkdownHeading_DoesNotNeedFallbackChunking()
    {
        var joined = new List<JoinedPageRecord> { JoinedPage("doc1", "# Heading\nSome content under it.") };
        var clean = BuildCleaner().Clean(joined);
        var pages = Empty<PageRecord>();
        var index = Empty<IndexRecord>();
        var join  = new JoinResult();

        var report = BuildValidator().Validate(pages, index, join, clean);

        CollectionAssert.DoesNotContain(report.DocumentsNeedingFallbackChunking.ToList(), "doc1");
    }

    [TestMethod]
    public void ReplacementCharacterInContent_IsTextQualityError()
    {
        var joined = new List<JoinedPageRecord> { JoinedPage("doc1", "Corrupted � text") };
        var clean = BuildCleaner().Clean(joined);
        var pages = Empty<PageRecord>();
        var index = Empty<IndexRecord>();
        var join  = new JoinResult();

        var report = BuildValidator().Validate(pages, index, join, clean);

        Assert.IsTrue(report.Issues.Any(i => i.Stage == "TextQuality" && i.Severity == "Error"));
        Assert.IsFalse(report.Passed);
    }

    [TestMethod]
    public void NonDutchLanguage_IsTextQualityWarning()
    {
        var joined = new List<JoinedPageRecord> { JoinedPage("doc1", "Some content", language: "en-US") };
        var clean = BuildCleaner().Clean(joined);
        var pages = Empty<PageRecord>();
        var index = Empty<IndexRecord>();
        var join  = new JoinResult();

        var report = BuildValidator().Validate(pages, index, join, clean);

        Assert.IsTrue(report.Issues.Any(i => i.Stage == "TextQuality" && i.Severity == "Warning" && i.Message.Contains("en-US")));
    }

    [TestMethod]
    public void ControlCharacterInContent_IsTextQualityError()
    {
        var joined = new List<JoinedPageRecord> { JoinedPage("doc1", "Corrupted  text") };
        var clean = BuildCleaner().Clean(joined);
        var pages = Empty<PageRecord>();
        var index = Empty<IndexRecord>();
        var join  = new JoinResult();

        var report = BuildValidator().Validate(pages, index, join, clean);

        Assert.IsTrue(report.Issues.Any(i => i.Stage == "TextQuality" && i.Severity == "Error" && i.Message.Contains("control/unassigned")));
        Assert.IsFalse(report.Passed);
    }

    [TestMethod]
    public void WhitespaceControlCharacters_AreNotFlaggedAsCorruption()
    {
        // \n, \r, \t are explicitly excluded from the control-character check - only
        // non-whitespace control/unassigned characters indicate corruption.
        var joined = new List<JoinedPageRecord> { JoinedPage("doc1", "Line one\nLine two\r\n\tIndented") };
        var clean = BuildCleaner().Clean(joined);
        var pages = Empty<PageRecord>();
        var index = Empty<IndexRecord>();
        var join  = new JoinResult();

        var report = BuildValidator().Validate(pages, index, join, clean);

        Assert.IsFalse(report.Issues.Any(i => i.Message.Contains("control/unassigned")));
    }

    [TestMethod]
    public void MarkdownTableWithInconsistentColumnCounts_ProducesWarningAndCountsAsDetectedTable()
    {
        var joined = new List<JoinedPageRecord> { JoinedPage("doc1",
            "| A | B | C |\n| --- | --- | --- |\n| 1 | 2 |\n") };
        var clean = BuildCleaner().Clean(joined);
        var pages = Empty<PageRecord>();
        var index = Empty<IndexRecord>();
        var join  = new JoinResult();

        var report = BuildValidator().Validate(pages, index, join, clean);

        Assert.IsTrue(report.Issues.Any(i => i.Stage == "TextQuality" && i.Severity == "Warning" && i.Message.Contains("inconsistent column counts")));
        Assert.AreEqual(1, report.DetectedTableCount);
    }

    [TestMethod]
    public void MarkdownTableWithConsistentColumnCounts_NoInconsistencyWarning()
    {
        var joined = new List<JoinedPageRecord> { JoinedPage("doc1",
            "| A | B |\n| --- | --- |\n| 1 | 2 |\n") };
        var clean = BuildCleaner().Clean(joined);
        var pages = Empty<PageRecord>();
        var index = Empty<IndexRecord>();
        var join  = new JoinResult();

        var report = BuildValidator().Validate(pages, index, join, clean);

        Assert.IsFalse(report.Issues.Any(i => i.Message.Contains("inconsistent column counts")));
        Assert.AreEqual(1, report.DetectedTableCount);
    }

    [TestMethod]
    public void MagnitudeShiftBeyondThreshold_FailsButPassedExcludingMagnitudeIsTrue()
    {
        var (pages, index, join, clean) = HappyPath(); // 1 cleaned record

        // Previous run had 100 - a drop to 1 is a -99% shift, way past the 20% threshold.
        var report = BuildValidator().Validate(pages, index, join, clean, previousRunCleanedCount: 100);

        Assert.IsFalse(report.Passed);
        Assert.IsTrue(report.PassedExcludingMagnitude);
        Assert.AreEqual(1, report.MagnitudeWarnings.Count);
    }

    [TestMethod]
    public void MagnitudeShiftWithinThreshold_Passes()
    {
        var (pages, index, join, clean) = HappyPath(); // 1 cleaned record

        var report = BuildValidator().Validate(pages, index, join, clean, previousRunCleanedCount: 1);

        Assert.IsTrue(report.Passed);
        Assert.AreEqual(0, report.MagnitudeWarnings.Count);
    }

    [TestMethod]
    public void NoPreviousRunCount_SkipsMagnitudeCheck()
    {
        var (pages, index, join, clean) = HappyPath();

        var report = BuildValidator().Validate(pages, index, join, clean, previousRunCleanedCount: null);

        Assert.AreEqual(0, report.MagnitudeWarnings.Count);
    }

    [TestMethod]
    public void AllPagesInactive_ZeroCleanedRecords_FailsEvenWithNoOtherErrors()
    {
        // Every row parses fine and the index matches - the only reason cleanResult ends
        // up empty is that the sole document is inactive. errorRate would be 0% and there's
        // no previous-run baseline for the magnitude check to compare against, so without
        // the explicit zero-records guard this would pass vacuously with nothing indexed.
        var pages = BuildExtractor().ExtractPages(ToStream(
            PagesHeader + "\n" +
            "doc1,Title,QC,Folder,20240101120000,0,Some content,rel,nl-NL\n"));
        var index = BuildExtractor().ExtractIndex(ToStream(
            IndexHeader + ",ACTIVE\n" +
            "doc1,Protocol,Summary,7,0,,[],false\n"));
        var join  = BuildJoiner().Join(pages.Records, index.Records);
        var clean = BuildCleaner().Clean(join.Joined);

        var report = BuildValidator().Validate(pages, index, join, clean);

        Assert.AreEqual(0, clean.Records.Count);
        Assert.IsFalse(report.Passed);
        Assert.IsTrue(report.ReconciliationProblems.Any(p => p.Contains("Zero cleaned records")));
    }
}
