namespace AgenticRagApp.Indexing.CU.Models;

// One node from a PDF's outline/bookmark tree.
//
// NOTHING POPULATES THIS TODAY. It was read by the PdfPig preflight, which was removed with the
// Document Intelligence pipeline - Content Understanding returns no outline, so
// PdfExtractionDocument.Bookmarks is always empty and the page-breadcrumb map built from it is
// always empty too. Kept because stored snapshots contain it and because an outline is a real
// signal worth recovering if a local pre-read ever returns.
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
