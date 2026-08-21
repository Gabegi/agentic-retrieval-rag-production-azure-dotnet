using Microsoft.VisualStudio.TestTools.UnitTesting;
using AgenticRagApp.Indexing.Csv.Models;
using AgenticRagApp.Indexing.Csv.Services;

namespace RagApp.UnitTests.CsvExtraction;

[TestClass]
public class CsvJoinerTests
{
    private static CsvJoiner BuildJoiner() => new();

    private static PageRecord Page(string docId, int pageIndex = 0, string content = "content") => new()
    {
        DocumentId  = docId,
        PageIndex   = pageIndex,
        PageContent = content,
        Title       = "Title",
    };

    private static IndexRecord Index(string docId, bool active = true, string documentTypeName = "Protocol") => new()
    {
        DocumentId       = docId,
        DocumentTypeName = documentTypeName,
        Active           = active,
    };

    [TestMethod]
    public void MatchedAndActive_IsAddedToJoined()
    {
        var result = BuildJoiner().Join([Page("doc1")], [Index("doc1")]);

        Assert.AreEqual(1, result.Joined.Count);
        Assert.AreEqual("doc1", result.Joined[0].DocumentId);
        Assert.AreEqual(0, result.Errors.Count);
        Assert.AreEqual(0, result.DataQualityWarnings.Count);
    }

    [TestMethod]
    public void PageWithNoMatchingIndex_IsError()
    {
        var result = BuildJoiner().Join([Page("doc1")], []);

        Assert.AreEqual(0, result.Joined.Count);
        Assert.AreEqual(1, result.Errors.Count);
        Assert.AreEqual("doc1", result.Errors[0].DocumentId);
    }

    [TestMethod]
    public void MultiplePagesForSameUnmatchedDoc_OnlyReportsErrorOnce()
    {
        var result = BuildJoiner().Join([Page("doc1", 0), Page("doc1", 1)], []);

        Assert.AreEqual(1, result.Errors.Count);
    }

    [TestMethod]
    public void InactiveIndexRecord_PageIsSkippedWithWarning()
    {
        var result = BuildJoiner().Join([Page("doc1")], [Index("doc1", active: false)]);

        Assert.AreEqual(0, result.Joined.Count);
        Assert.AreEqual(1, result.DataQualityWarnings.Count);
        Assert.AreEqual(1, result.InactivePagesSkipped);
        Assert.AreEqual(0, result.Errors.Count);
    }

    [TestMethod]
    public void InactiveIndexRecord_DoesNotAlsoAppearInSkippedIndexRecords()
    {
        var result = BuildJoiner().Join([Page("doc1")], [Index("doc1", active: false)]);

        // Deliberate: an inactive-but-matched doc counts as "used", so it shouldn't
        // double up in SkippedIndexRecords - it's already tracked via the warning.
        Assert.AreEqual(0, result.SkippedIndexRecords.Count);
    }

    [TestMethod]
    public void MultiplePagesForSameInactiveDoc_CountsEachPageButWarnsOnce()
    {
        var result = BuildJoiner().Join([Page("doc1", 0), Page("doc1", 1)], [Index("doc1", active: false)]);

        Assert.AreEqual(2, result.InactivePagesSkipped);
        Assert.AreEqual(1, result.DataQualityWarnings.Count);
    }

    [TestMethod]
    public void IndexRecordWithNoMatchingPages_IsSkipped()
    {
        var result = BuildJoiner().Join([], [Index("doc1")]);

        Assert.AreEqual(1, result.SkippedIndexRecords.Count);
        Assert.AreEqual("doc1", result.SkippedIndexRecords[0].DocumentId);
    }

    [TestMethod]
    public void DuplicateIndexDocumentId_KeepsFirstOccurrenceAndWarnsOnce()
    {
        var result = BuildJoiner().Join(
            [Page("doc1")],
            [Index("doc1", documentTypeName: "First"), Index("doc1", documentTypeName: "Second")]);

        Assert.AreEqual(1, result.Joined.Count);
        Assert.AreEqual("First", result.Joined[0].DocumentTypeName);
        Assert.AreEqual(1, result.DataQualityWarnings.Count);
    }

    [TestMethod]
    public void DuplicateIndexDocumentId_RepeatedManyTimes_WarnsOnlyOnce()
    {
        var index = Enumerable.Range(0, 50).Select(_ => Index("doc1")).ToList();

        var result = BuildJoiner().Join([Page("doc1")], index);

        Assert.AreEqual(1, result.DataQualityWarnings.Count);
    }

    [TestMethod]
    public void DocumentIdMatching_IsCaseInsensitive()
    {
        var result = BuildJoiner().Join([Page("DOC1")], [Index("doc1")]);

        Assert.AreEqual(1, result.Joined.Count);
    }

    [TestMethod]
    public void AllPagesForDocument_AreJoinedToSameIndexRecord()
    {
        var result = BuildJoiner().Join([Page("doc1", 0), Page("doc1", 1), Page("doc1", 2)], [Index("doc1")]);

        Assert.AreEqual(3, result.Joined.Count);
        Assert.IsTrue(result.Joined.All(r => r.DocumentTypeName == "Protocol"));
    }

    [TestMethod]
    public void MixedBatch_ClassifiesEachDocumentIndependently()
    {
        var pages = new[]
        {
            Page("matched"),
            Page("inactive"),
            Page("notfound"),
        };
        var index = new[]
        {
            Index("matched", active: true),
            Index("inactive", active: false),
            Index("orphan", active: true), // no pages -> skipped
        };

        var result = BuildJoiner().Join(pages, index);

        Assert.AreEqual(1, result.Joined.Count);
        Assert.AreEqual("matched", result.Joined[0].DocumentId);
        Assert.AreEqual(1, result.Errors.Count);
        Assert.AreEqual("notfound", result.Errors[0].DocumentId);
        Assert.AreEqual(1, result.InactivePagesSkipped);
        Assert.AreEqual(1, result.SkippedIndexRecords.Count);
        Assert.AreEqual("orphan", result.SkippedIndexRecords[0].DocumentId);
    }
}
