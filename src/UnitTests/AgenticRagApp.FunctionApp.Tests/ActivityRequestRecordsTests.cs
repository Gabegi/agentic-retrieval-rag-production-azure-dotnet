using AgenticRagApp.Functions;

namespace RagApp.UnitTests.FunctionApp;

[TestClass]
public class ActivityRequestRecordsTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 7, 27, 10, 35, 0, TimeSpan.Zero);

    [TestMethod]
    public void ExtractRequest_Constructor_PropagatesAllFields()
    {
        var request = new ExtractRequest(true, "docs-blob", "instance-1", StartedAt);

        Assert.IsTrue(request.ForceReindex);
        Assert.AreEqual("docs-blob", request.OutputBlob);
        Assert.AreEqual("instance-1", request.InstanceId);
        Assert.AreEqual(StartedAt, request.StartedAt);
    }

    [TestMethod]
    public void ExtractRequest_RecordEquality_SameValues_AreEqual()
    {
        var a = new ExtractRequest(false, "docs-blob", "instance-1", StartedAt);
        var b = new ExtractRequest(false, "docs-blob", "instance-1", StartedAt);

        Assert.AreEqual(a, b);
    }

    [TestMethod]
    public void ChunkRequest_Constructor_PropagatesAllFields()
    {
        var request = new ChunkRequest("docs-blob", "chunks-blob", "instance-1", StartedAt);

        Assert.AreEqual("docs-blob", request.InputBlob);
        Assert.AreEqual("chunks-blob", request.OutputBlob);
        Assert.AreEqual("instance-1", request.InstanceId);
        Assert.AreEqual(StartedAt, request.StartedAt);
    }

    [TestMethod]
    public void ChunkRequest_RecordEquality_DifferentValues_AreNotEqual()
    {
        var a = new ChunkRequest("docs-blob", "chunks-blob", "instance-1", StartedAt);
        var b = new ChunkRequest("docs-blob", "chunks-blob", "instance-2", StartedAt);

        Assert.AreNotEqual(a, b);
    }

    [TestMethod]
    public void EmbedUploadRequest_Constructor_PropagatesAllFields()
    {
        var staleIds = new List<string> { "doc1", "doc2" };

        var request = new EmbedUploadRequest("chunks-blob", staleIds, "instance-1", StartedAt);

        Assert.AreEqual("chunks-blob", request.ChunksBlob);
        CollectionAssert.AreEqual(staleIds, request.StaleDocumentIds.ToList());
        Assert.AreEqual("instance-1", request.InstanceId);
        Assert.AreEqual(StartedAt, request.StartedAt);
    }

    [TestMethod]
    public void EmbedUploadRequest_Constructor_AllowsEmptyStaleDocumentIds()
    {
        var request = new EmbedUploadRequest("chunks-blob", [], "instance-1", StartedAt);

        Assert.AreEqual(0, request.StaleDocumentIds.Count);
    }
}
