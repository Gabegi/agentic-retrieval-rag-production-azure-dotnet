namespace AgenticRagApp.Indexing.CU.Models;

// One hyperlink CU detected in the document's digital content (DocumentContent.Hyperlinks).
// Mapped by CuHyperlinkHelper.
//
// Content is the display text, Uri the target - both nullable because the service guarantees
// neither (an image link has no display text; a malformed target may come back without a Uri).
// Offset anchors into the markdown (utf16), nullable per this folder's convention.
public sealed record HyperlinkInfo(
    string? Content,
    string? Uri,
    int?    Offset,
    int     PageNumber);
