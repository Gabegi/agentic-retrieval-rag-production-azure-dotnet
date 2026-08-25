using System.Security.Cryptography;
using Azure.AI.ContentUnderstanding;
using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Indexing.CU.Models;

// Everything one document produced, or the reason it produced nothing.
//
// The unit of exchange between extraction's three parts: ExtractionService.ExtractFileAsync
// produces one of these per blob, its own run loop collects them across the run, and
// ExtractionOutputBuilder turns the collection into the stage's output. Failures are kept rather
// than dropped - a document that could not be extracted is a fact the run report has to carry -
// and every reader distinguishes them on Ok alone.
//
// Sits one level up from AnalyzeOutcome: that is what one analyze CALL returned, this is what one
// FILE produced, mapping included.
internal sealed record ExtractedFile(
    bool                         Ok,
    string                       BlobName,
    string?                      Content,
    IReadOnlyList<PageSpan>?     PageSpans,
    PdfDocumentStructure?        Structure,
    string?                      Title,
    DocumentProfile?             Profile,
    string?                      Language,
    AnalyzeUsageDetails?         Usage,
    PipelineIssue?               Error,
    IReadOnlyList<PipelineIssue> Warnings)
{
    // SHA-256 over the raw downloaded bytes. Null means there were no bytes to hash, which is
    // exactly the download-failure case - a file that downloaded but failed to analyze still
    // carries its hash, since the hash is taken before anything is submitted.
    //
    // Measurement only, and it leaves this record almost immediately: ExtractionOutputBuilder
    // .BuildContentHashes lifts it onto PdfExtractionOutput.ContentHashes, from where the reporter
    // writes it into the per-document facts report and logs the distinct-vs-total summary. See
    // BuildContentHashes for why it is deliberately not a cache key.
    public string? ContentHash { get; init; }

    // What fills ContentHash. Stable over the document's raw bytes: same file content -> same
    // hash, regardless of the blob's file name. Lives here rather than on the caller so the
    // definition of the value sits with the property that carries it.
    public static string ComputeContentHash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    // A factory rather than nine positional nulls repeated at every failure site.
    public static ExtractedFile Failed(
        string blobName, PipelineIssue error, IReadOnlyList<PipelineIssue>? warnings = null) =>
        new(false, blobName, null, null, null, null, null, null, null, error, warnings ?? []);
}
