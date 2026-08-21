namespace AgenticRagApp.Indexing.CU.Models;

// Dev-only proof that PdfCleaner actually changed something, not just "the output looks
// clean" — RawContent is the page as PdfCleaner received it (page.PageContent), CleanedContent
// is what it returned. Sampled from the same run's PdfQualityGateResult.SpotCheckSample pages
// so a human can diff the two directly instead of trusting counters alone.
public record CleaningSpotCheckEntry
{
    public string BlobName      { get; init; } = "";
    public int    PageNumber    { get; init; }
    public string RawContent    { get; init; } = "";
    public string CleanedContent { get; init; } = "";
}
