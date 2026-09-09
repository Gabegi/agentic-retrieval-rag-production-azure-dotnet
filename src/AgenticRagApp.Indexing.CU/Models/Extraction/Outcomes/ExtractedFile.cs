using System.Security.Cryptography;
using AgenticRagApp.Common.Models;
using AgenticRagApp.Infrastructure.Clients.ContentUnderstanding;

namespace AgenticRagApp.Indexing.CU.Models;

// Everything one document produced, or the reason it produced nothing.
//
// The unit of exchange between extraction's three parts: ExtractionService.ExtractFileAsync
// produces one of these per blob, its own run loop collects them across the run, and
// ExtractionOutputBuilder turns the collection into the stage's output. Failures are kept rather
// than dropped - a document that could not be extracted is a fact the run report has to carry -
// and every reader distinguishes them on Ok alone.
//
// Content is the analyzer's raw markdown. PageSpans, Structure (headings + boilerplate) and
// Title are derived from that markdown by MarkdownStructureMapper - one coordinate system, the
// string chunking cuts. Profile and Language stay null: nothing measures them on this backend.
// Usage is the analysis's billed cost, read off the LRO Operation via GetUsage() and carried
// through ContentAnalysis; null only when the completed operation had no readable usage payload.
internal sealed record ExtractedFile(
    bool                         Ok,
    string                       BlobName,
    string?                      Content,
    IReadOnlyList<PageSpan>?     PageSpans,
    PdfDocumentStructure?        Structure,
    string?                      Title,
    DocumentProfile?             Profile,
    string?                      Language,
    CuUsage?                     Usage,
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

    // Wall-clock for this file end to end (download + analyze + map), measured by the run loop -
    // the only place that sees all three. Measurement only, same lifecycle as ContentHash:
    // ExtractionOutputBuilder lifts it onto PdfExtractionOutput.Durations, and the reporter
    // writes it into the per-document facts report. Null means the loop never set it, which is
    // only the case in tests that build ExtractedFiles directly.
    public long? DurationMs { get; init; }

    // How well the service says it read this document, summarised by the mapper - see
    // WordConfidenceSummary. Measurement only, same lifecycle as ContentHash and DurationMs:
    // ExtractionOutputBuilder lifts it onto PdfExtractionOutput.WordConfidences and the reporter
    // writes it into the per-document facts report. Null means the response carried no words
    // with a confidence, which is blank rather than zero.
    public WordConfidenceSummary? WordConfidence { get; init; }

    // CU's generated whole-document summary (fields.Summary), mapped by CUHelper. Measurement
    // and metadata only, same lifecycle as the three properties above: ExtractionOutputBuilder
    // lifts it onto PdfExtractionOutput.Summaries and the reporter writes it into the
    // per-document facts report. It is deliberately NOT indexed - see DocumentSummary. Null
    // means the response carried no Summary field, or one carrying no text.
    public DocumentSummary? Summary { get; init; }

    // How sure AI Language was about Language (the positional field above). Report-only and
    // deliberately separate from the value: Language feeds the index field, this feeds the
    // facts report, and NO rule reads it - see IDocumentLanguageDetector for why no threshold
    // is invented here. Null whenever Language is null, and also when a detection carried no
    // score.
    public double? LanguageConfidence { get; init; }

    // A factory rather than nine positional nulls repeated at every failure site.
    public static ExtractedFile Failed(
        string blobName, PipelineIssue error, IReadOnlyList<PipelineIssue>? warnings = null) =>
        new(false, blobName, null, null, null, null, null, null, null, error, warnings ?? []);
}
