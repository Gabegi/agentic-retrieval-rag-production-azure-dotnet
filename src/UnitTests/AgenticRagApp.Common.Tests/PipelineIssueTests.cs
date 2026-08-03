using AgenticRagApp.Common.Models;

namespace RagApp.UnitTests.Common;

[TestClass]
public class PipelineIssueTests
{
    [TestMethod]
    public void Error_Factory_SetsSeverityAndFields()
    {
        var issue = PipelineIssue.Error(PipelineStage.Clean, "doc1.pdf", "mojibake repaired");

        Assert.AreEqual(PipelineStage.Clean, issue.Stage);
        Assert.AreEqual(IssueSeverity.Error, issue.Severity);
        Assert.AreEqual("doc1.pdf", issue.DocumentId);
        Assert.AreEqual("mojibake repaired", issue.Message);
        Assert.IsTrue(issue.IsError);
        Assert.IsFalse(issue.IsWarning);
    }

    [TestMethod]
    public void Warning_Factory_SetsSeverityAndFields()
    {
        var issue = PipelineIssue.Warning(PipelineStage.TextQuality, "doc1.pdf", "blank page");

        Assert.AreEqual(IssueSeverity.Warning, issue.Severity);
        Assert.IsTrue(issue.IsWarning);
        Assert.IsFalse(issue.IsError);
    }

    // A file-level failure never gets as far as identifying a document, so DocumentId is
    // nullable by design - it means "not applicable", not "unknown".
    [TestMethod]
    public void DocumentId_IsNullable_ForFileLevelFailures()
    {
        var issue = PipelineIssue.Error(PipelineStage.ParsePages, null, "no document context available");

        Assert.IsNull(issue.DocumentId);
        Assert.AreEqual("no document context available", issue.Message);
    }

    // RowNumber and Reason are opt-in: anything not row-addressable, or without a
    // structured failure category, leaves them null rather than inventing a value.
    [TestMethod]
    public void RowNumberAndReason_DefaultToNull()
    {
        var issue = PipelineIssue.Warning(PipelineStage.Join, "doc1", "message");

        Assert.IsNull(issue.RowNumber);
        Assert.IsNull(issue.Reason);
    }

    [TestMethod]
    public void RecordEquality_SameValues_AreEqual()
    {
        var a = PipelineIssue.Error(PipelineStage.Clean, "doc1.pdf", "message");
        var b = PipelineIssue.Error(PipelineStage.Clean, "doc1.pdf", "message");

        Assert.AreEqual(a, b);
    }

    // Severity used to be encoded in the type name (CleaningError vs CleaningWarning),
    // which meant two identical shapes. It is now a value, and it participates in equality.
    [TestMethod]
    public void RecordEquality_DifferentSeverity_AreNotEqual()
    {
        var error   = PipelineIssue.Error(PipelineStage.Clean, "doc1.pdf", "message");
        var warning = PipelineIssue.Warning(PipelineStage.Clean, "doc1.pdf", "message");

        Assert.AreNotEqual(error, warning);
    }

    [TestMethod]
    public void RecordEquality_DifferentStage_AreNotEqual()
    {
        var a = PipelineIssue.Error(PipelineStage.Clean, "doc1.pdf", "message");
        var b = PipelineIssue.Error(PipelineStage.Join, "doc1.pdf", "message");

        Assert.AreNotEqual(a, b);
    }
}
