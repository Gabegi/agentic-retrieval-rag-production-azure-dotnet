using AgenticRagApp.Common.Models;
using Azure.AI.ContentUnderstanding;

namespace AgenticRagApp.Indexing.CU.Models;

// The result of one Content Understanding analyze call, after polling has finished.
//
// Ok = false covers the ways the call can fail to produce something usable: an exception,
// throttling that outlived its budget, or a response in the wrong string encoding. All of them
// carry an Error; none of them reach the mapper.
//
// The list used to be longer - a document with no markdown, no pages, more than one content, or
// YAML front matter each failed here too. Those checks were removed on purpose while the flow is
// being settled; such a response now reaches the mapper and fails (or does not) on its own terms.
//
// This is the type AnalysisPoller's `validate` and `fail` delegates are instantiated with - the
// poller itself is generic over both the result and this outcome, which is what let it survive
// the Document Intelligence removal untouched.
public sealed record AnalyzeOutcome(bool Ok, AnalysisResult? Result, PipelineIssue? Error)
{
    public IReadOnlyList<AnalysisWarning> Warnings { get; init; } = [];

    // What the service says this call actually consumed - pages at the standard content tier,
    // plus contextualization/completion tokens when figure description is on. Only set on the
    // Ok path, because it is read off the completed Operation rather than the AnalysisResult
    // (see AnalyzeOperationExtensions.GetUsage), and there is no operation to read on a failure
    // that never completed.
    //
    // This replaces the Document Intelligence era's CostPerPage = 0.01m local estimate: that
    // constant was a guess maintained by hand against a pricing page, and DI's API never
    // returned actual usage. This is the service's own number.
    public AnalyzeUsageDetails? Usage { get; init; }

    public static AnalyzeOutcome Fail(string blobName, string message, PdfOpenFailureReason reason) =>
        new(false, null, PipelineIssue.Error(
            PipelineStage.ParsePages, blobName, message, reason: reason));
}
