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

    // Carried through cleaning rather than recomputed: the flag is a join between DI's
    // figure list and its zero-word pages (GetPictureOnlyPagesHelper), and neither input
    // survives into the cleaned text - a page that lost all its content to cleaning looks
    // identical to a genuinely blank one by the time it gets here. Previously dropped at
    // this boundary, which is why the document-level gate was the only one anything
    // downstream could see (action-plan.md C4).
    public bool IsPictureOnlyPage { get; init; }
}
