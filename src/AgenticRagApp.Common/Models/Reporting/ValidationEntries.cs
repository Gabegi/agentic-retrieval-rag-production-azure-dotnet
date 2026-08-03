namespace AgenticRagApp.Common.Models;

// Report-shaped projection of a cleaned record, sampled for manual inspection. Lives in
// Common rather than Observability because ExtractionOutputBase carries it, and Common
// must not depend on the reporting project.
//
// ValidationIssueEntry used to sit alongside this - a field-for-field copy of
// ValidationIssue that existed only to cross the Common/Observability assembly boundary.
// Both are now PipelineIssue, and the conversion loops it required are gone.
public record SpotCheckEntry(string DocumentId, string Title, string ContentPreview);
