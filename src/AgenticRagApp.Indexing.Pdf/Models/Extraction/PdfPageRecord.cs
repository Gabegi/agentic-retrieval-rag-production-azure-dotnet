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

    // C5 (pre-chunking-action-items.md) - this page has at least one figure AND DI found
    // no extractable words on it (or no content survived cleanup) - a page-level analogue
    // of the document-level Picture route, for the mixed-document case a whole-document
    // threshold structurally can't catch (38 normal pages + 2 diagram pages). Set by
    // GetPictureOnlyPagesHelper once figures are known, after this record is first built -
    // defaults false, never a real "not picture-only" claim until that join has run.
    public bool IsPictureOnlyPage { get; init; }
}
