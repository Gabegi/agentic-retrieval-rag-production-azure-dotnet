using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Indexing.CU.Models;

// One non-fatal warning the analysis service attached to a document (e.g. a page whose OCR
// partially failed) - distinct from the empty-document case, which the analyzer treats as an
// outright failure. Also carries this pipeline's own diagnostics, such as the non-BMP character
// count, so every "worth knowing, not worth failing over" signal has one shape.
//
// Deliberately this project's own record rather than the SDK's warning type: callers of these
// models should not need a reference to the Azure SDK, and this outlived one backend already.
public sealed record AnalysisWarning(string? Code, string? Message, string? Target)
{
    // The same warning, rebadged for the pipeline's own issue list. Stage is ParsePages because
    // these come from the analyze call that produces the page records - not from local gates.
    public PipelineIssue ToPipelineIssue(string blobName) =>
        PipelineIssue.Warning(
            PipelineStage.ParsePages,
            blobName,
            string.IsNullOrEmpty(Code) ? Message ?? "" : $"[{Code}] {Message}");
}
