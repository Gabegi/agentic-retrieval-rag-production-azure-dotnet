namespace AgenticRagApp.Indexing.Pdf.Models;

// Dev-only proof that PdfCleaner actually changed something, not just "the output looks
// clean" — RawContent is the page as PdfCleaner received it (page.PageContent), CleanedContent
// is what it returned. Sampled from the same run's PdfValidationReport.SpotCheckSample pages
// so a human can diff the two directly instead of trusting counters alone.
public class CleaningSpotCheckEntry
{
    public string BlobName      { get; set; } = "";
    public int    PageNumber    { get; set; }
    public string RawContent    { get; set; } = "";
    public string CleanedContent { get; set; } = "";
}
