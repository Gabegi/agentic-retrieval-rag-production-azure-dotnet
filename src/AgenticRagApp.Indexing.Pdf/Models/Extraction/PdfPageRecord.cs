namespace AgenticRagApp.Indexing.Pdf.Models;

// One page's raw extracted content from a single PDF file — mirrors CSV's PageRecord.
// PageContent is already markdown-flavored ("## " headings, pipe-row tables) by the
// time it leaves the extractor, same shape CSV's PageRecord.PageContent arrives in.
// Title is the whole document's title (same value on every page of one file) - set once
// when the pages are built, not joined in from a separate index record.
public record PdfPageRecord
{
    public string BlobName    { get; init; } = "";
    public int    PageNumber  { get; init; }

    // Cleaned, not offset-addressable - GetPages strips noise comments before this is
    // set, so it's no longer an exact RawContent substring. Match any structural Offset
    // (Heading, TableInfo, SectionInfo, ...) against RawContent, never this field.
    public string PageContent { get; init; } = "";
    public string Title       { get; init; } = "";
}
