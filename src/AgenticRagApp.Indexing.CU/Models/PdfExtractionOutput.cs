using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Indexing.CU.Models;

public sealed record PdfExtractionOutput(IReadOnlyList<PdfExtractionDocument> Docs) : ExtractionOutputBase
{
    // What the extraction service says this run actually consumed, summed from Content
    // Understanding's own per-document usage. Carried on this record rather than
    // ExtractionOutputBase because it is specific to this backend - the CSV pipeline has no
    // analyze call and no usage to report.
    //
    // Reported in the units the service bills in, not converted to a currency figure: CU meters
    // standard pages and contextualization tokens separately, and the DI-era $0.01-per-page
    // constant this replaces was a hand-maintained guess that the API never confirmed.
    //
    // Null means no usage was readable this run (every document failed, or the SDK could not
    // produce a usage object) - distinct from zero, which is a real "nothing was billed".
    public long? BilledPagesStandard { get; init; }
    public long? BilledContextualizationTokens { get; init; }

    // Every document this run hashed, in blob-name order. Report-only: nothing downstream of
    // extraction reads it, and ExtractionReporter is its single consumer - it writes the hashes
    // into the per-document facts report and logs the distinct-vs-total summary.
    //
    // Empty rather than null when nothing hashed, since "no documents" and "no hashes" are the
    // same fact here - unlike the billed-usage fields above.
    public IReadOnlyList<DocumentContentHash> ContentHashes { get; init; } = [];
}
