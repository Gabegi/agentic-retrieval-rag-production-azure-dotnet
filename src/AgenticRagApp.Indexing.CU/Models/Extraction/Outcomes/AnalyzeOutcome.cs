using Azure.AI.DocumentIntelligence;
using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Indexing.CU.Models;

// Result of calling the (paid) Document Intelligence analyze API once:
// - Ok = true  -> Result contains a successful, non-empty analysis (at least one page -
//   a zero-page result is deliberately folded into Ok = false, see DIAnalyzeDocumentAsync).
// - Ok = false -> Error contains a typed reason instead of throwing an exception.
//   This lets callers check Error.Reason and decide what to do
//   (e.g. "Throttled" is worth retrying, "DiServiceError" probably isn't).
public sealed record AnalyzeOutcome(bool Ok, AnalyzeResult? Result, PipelineIssue? Error)
{
    // Non-fatal findings from the analyze call itself (e.g. the non-BMP character
    // check) - only ever populated when Ok is true, since Error already covers the
    // failure case. Empty (not null) otherwise.
    public IReadOnlyList<AnalysisWarning> Warnings { get; init; } = [];
}
