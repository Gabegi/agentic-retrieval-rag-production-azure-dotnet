namespace AgenticRagApp.Indexing.Pdf.Models;

// Structurally identical to PdfPageRecord, deliberately - do not merge them. The distinct
// type is what marks a page as having been through PdfCleaner, and that matters: an
// un-cleaned PdfPageRecord.PageContent is offset-addressable against RawContent, and this
// one is NOT (see PdfPageRecord.PageContent's own comment). Two types with the same shape
// is the right call when the difference is a processing stage, unlike the old
// CleaningError/CleaningWarning pair, where the difference was a value (severity) and
// belonged in a field.
public record CleanedPdfPageRecord
{
    public string BlobName    { get; init; } = "";
    public int    PageNumber  { get; init; }
    public string PageContent { get; init; } = "";
    public string Title       { get; init; } = "";
}
