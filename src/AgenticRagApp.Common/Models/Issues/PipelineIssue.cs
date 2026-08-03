namespace AgenticRagApp.Common.Models;

// One problem found by one pipeline step, for one document (or one row).
//
// Replaces CleaningError, CleaningWarning, ExtractionError, ExtractionWarning,
// ValidationIssue, ValidationIssueEntry and PipelineIssueBase - seven types whose only
// real differences were a severity, an optional row number and an optional structured
// reason. Two of them (CleaningError/CleaningWarning) were byte-for-byte identical.
//
// The three nullable fields each mean "not applicable to this finding", not "unknown":
// - DocumentId: null for a file-level failure that never got as far as identifying a document.
// - RowNumber:  null for anything not row-addressable (a whole-file failure, a page-level check).
// - Reason:     set only by steps that distinguish failure categories at the point of failure
//               (currently PdfDocumentValidator.TryOpenAndValidate). Free-text-only findings
//               leave it null. See OpenFailureReasonBase for why this isn't an enum.
public sealed record PipelineIssue(
    PipelineStage          Stage,
    IssueSeverity          Severity,
    string?                DocumentId,
    string                 Message,
    int?                   RowNumber = null,
    OpenFailureReasonBase? Reason    = null)
{
    public static PipelineIssue Error(
        PipelineStage stage,
        string? documentId,
        string message,
        int? rowNumber = null,
        OpenFailureReasonBase? reason = null) =>
        new(stage, IssueSeverity.Error, documentId, message, rowNumber, reason);

    public static PipelineIssue Warning(
        PipelineStage stage,
        string? documentId,
        string message,
        int? rowNumber = null,
        OpenFailureReasonBase? reason = null) =>
        new(stage, IssueSeverity.Warning, documentId, message, rowNumber, reason);

    public bool IsError   => Severity is IssueSeverity.Error;
    public bool IsWarning => Severity is IssueSeverity.Warning;
}
