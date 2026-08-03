namespace AgenticRagApp.Indexing.Pdf.Models;

// One node from a PDF's outline/bookmark tree, as read by PdfNativeMetadataExtractor.
// PageNumber is null when the node's destination couldn't be resolved to a page in
// this document - IsExternal/IsEmbedded tell PdfSectionBreadCrumbBuilder which PdfPig
// node type produced that null, for separate diagnostics (both already collapse to
// PageNumber=null by the time this record exists, so the distinction would otherwise
// be lost):
//  - IsExternal: ExternalBookmarkNode - points at another file, not a page here.
//  - IsEmbedded: EmbeddedBookmarkNode - points at a file embedded in this PDF.
// PdfPig also has an internal ContainerBookmarkNode (purely organizational, no target)
// but it isn't a public type, so it can't be distinguished here - it collapses into the
// same bucket as a DocumentBookmarkNode whose destination just didn't resolve.
public sealed record Bookmark(string Title, int Level, int? PageNumber, bool IsExternal, bool IsEmbedded = false);
