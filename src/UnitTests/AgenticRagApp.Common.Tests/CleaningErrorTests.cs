using AgenticRagApp.Common.Models;

namespace RagApp.UnitTests.Common;

[TestClass]
public class CleaningErrorTests
{
    [TestMethod]
    public void Constructor_SetsDocumentIdAndMessage()
    {
        var error = new CleaningError("doc1.pdf", "mojibake repaired");

        Assert.AreEqual("doc1.pdf", error.DocumentId);
        Assert.AreEqual("mojibake repaired", error.Message);
    }

    [TestMethod]
    public void Constructor_AllowsNullDocumentId()
    {
        var error = new CleaningError(null, "no document context available");

        Assert.IsNull(error.DocumentId);
        Assert.AreEqual("no document context available", error.Message);
    }

    [TestMethod]
    public void IsAssignableTo_PipelineIssueBase()
    {
        PipelineIssueBase error = new CleaningError("doc1.pdf", "message");

        Assert.AreEqual("doc1.pdf", error.DocumentId);
        Assert.AreEqual("message", error.Message);
    }

    [TestMethod]
    public void RecordEquality_SameValues_AreEqual()
    {
        var a = new CleaningError("doc1.pdf", "message");
        var b = new CleaningError("doc1.pdf", "message");

        Assert.AreEqual(a, b);
    }

    [TestMethod]
    public void RecordEquality_DifferentValues_AreNotEqual()
    {
        var a = new CleaningError("doc1.pdf", "message");
        var b = new CleaningError("doc2.pdf", "message");

        Assert.AreNotEqual(a, b);
    }
}
