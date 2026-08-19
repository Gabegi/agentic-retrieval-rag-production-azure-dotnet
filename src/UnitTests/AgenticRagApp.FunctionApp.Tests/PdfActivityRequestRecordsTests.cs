using AgenticRagApp.Functions;

namespace RagApp.UnitTests.FunctionApp;

[TestClass]
public class PdfActivityRequestRecordsTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 7, 27, 10, 35, 0, TimeSpan.Zero);

    [TestMethod]
    public void PdfExtractRequest_Constructor_PropagatesAllFields()
    {
        var request = new PdfExtractRequest(true, "docs-blob", "stale-ids-blob", "instance-1", StartedAt);

        Assert.IsTrue(request.ForceReindex);
        Assert.AreEqual("docs-blob", request.OutputBlob);
        Assert.AreEqual("stale-ids-blob", request.StaleIdsBlob);
        Assert.AreEqual("instance-1", request.InstanceId);
        Assert.AreEqual(StartedAt, request.StartedAt);
    }

    [TestMethod]
    public void PdfExtractRequest_RecordEquality_SameValues_AreEqual()
    {
        var a = new PdfExtractRequest(false, "docs-blob", "stale-ids-blob", "instance-1", StartedAt);
        var b = new PdfExtractRequest(false, "docs-blob", "stale-ids-blob", "instance-1", StartedAt);

        Assert.AreEqual(a, b);
    }

    [TestMethod]
    public void PdfChunkRequest_Constructor_PropagatesAllFields()
    {
        var request = new PdfChunkRequest("docs-blob", "chunks-blob", "moves-blob", "instance-1", StartedAt);

        Assert.AreEqual("docs-blob", request.InputBlob);
        Assert.AreEqual("chunks-blob", request.OutputBlob);
        Assert.AreEqual("moves-blob", request.FamilyMovesBlob);
        Assert.AreEqual("instance-1", request.InstanceId);
        Assert.AreEqual(StartedAt, request.StartedAt);
    }

    [TestMethod]
    public void PdfChunkRequest_RecordEquality_DifferentValues_AreNotEqual()
    {
        var a = new PdfChunkRequest("docs-blob", "chunks-blob", "moves-blob", "instance-1", StartedAt);
        var b = new PdfChunkRequest("docs-blob", "chunks-blob", "moves-blob", "instance-2", StartedAt);

        Assert.AreNotEqual(a, b);
    }

    [TestMethod]
    public void PdfEmbedUploadRequest_Constructor_PropagatesAllFields()
    {
        var request = new PdfEmbedUploadRequest("chunks-blob", "stale-ids-blob", "moves-blob", "instance-1", StartedAt);

        Assert.AreEqual("chunks-blob", request.ChunksBlob);
        Assert.AreEqual("stale-ids-blob", request.StaleIdsBlob);
        Assert.AreEqual("moves-blob", request.FamilyMovesBlob);
        Assert.AreEqual("instance-1", request.InstanceId);
        Assert.AreEqual(StartedAt, request.StartedAt);
    }
}
