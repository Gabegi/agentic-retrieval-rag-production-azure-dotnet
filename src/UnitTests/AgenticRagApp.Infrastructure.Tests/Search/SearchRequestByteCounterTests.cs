using AgenticRagApp.Infrastructure.Clients.Search;

namespace RagApp.UnitTests.Infrastructure.Search;

// The counter behind the upload-payload report field (D203 §8). What it has to get right: which
// bucket a request lands in, that a request the SDK could not size is counted as unmeasured
// rather than as zero bytes, and that a snapshot is a consistent read of all five numbers.
[TestClass]
public class SearchRequestByteCounterTests
{
    [TestMethod]
    public void IsIndexDocsPath_MatchesBothSpellingsOfThePushApi_CaseInsensitively()
    {
        Assert.IsTrue(SearchRequestByteCounter.IsIndexDocsPath("/indexes('chunks')/docs/search.index"));
        Assert.IsTrue(SearchRequestByteCounter.IsIndexDocsPath("/indexes/chunks/docs/index"));
        Assert.IsTrue(SearchRequestByteCounter.IsIndexDocsPath("/indexes('chunks')/DOCS/SEARCH.INDEX"));
        Assert.IsFalse(SearchRequestByteCounter.IsIndexDocsPath("/indexes('chunks')/docs/search.post.search"));
        Assert.IsFalse(SearchRequestByteCounter.IsIndexDocsPath("/indexes('chunks')/stats"));
        Assert.IsFalse(SearchRequestByteCounter.IsIndexDocsPath("/indexes('chunks')"));
    }

    [TestMethod]
    public void Record_PushRequests_AccumulateBytesAndCount()
    {
        var counter = new SearchRequestByteCounter();

        counter.Record("/indexes('chunks')/docs/search.index", 40_000_000);
        counter.Record("/indexes('chunks')/docs/search.index", 28_000_000);

        var s = counter.Read();
        Assert.AreEqual(68_000_000L, s.IndexDocsBytes);
        Assert.AreEqual(2L, s.IndexDocsRequests);
        Assert.AreEqual(0L, s.IndexDocsUnmeasured);
        Assert.AreEqual(0L, s.OtherRequests);
    }

    // A request whose length the SDK cannot compute is still a request, and it makes the byte
    // total an undercount for its window. It is counted as unmeasured, never as 0 bytes.
    [TestMethod]
    public void Record_PushRequestWithoutALength_IsCountedAsUnmeasured()
    {
        var counter = new SearchRequestByteCounter();

        counter.Record("/indexes('chunks')/docs/search.index", 1_000);
        counter.Record("/indexes('chunks')/docs/search.index", null);

        var s = counter.Read();
        Assert.AreEqual(1_000L, s.IndexDocsBytes);
        Assert.AreEqual(2L, s.IndexDocsRequests);
        Assert.AreEqual(1L, s.IndexDocsUnmeasured);
    }

    [TestMethod]
    public void Record_NonPushRequests_LandInTheOtherBucket()
    {
        var counter = new SearchRequestByteCounter();

        counter.Record("/indexes('chunks')/docs/search.post.search", 512);
        counter.Record("/indexes('chunks')/stats", null);

        var s = counter.Read();
        Assert.AreEqual(0L, s.IndexDocsRequests);
        Assert.AreEqual(2L, s.OtherRequests);
        Assert.AreEqual(512L, s.OtherBytes);
    }
}
