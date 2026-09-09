namespace AgenticRagApp.Indexing.CU.Models;

// Everything extraction produced about the pages a chunk covers that Azure AI Search has no
// schema for - nested objects like TableInfo's cells. ChunkObject derives table_count,
// has_table and figure_captions from Tables/Figures; the rest is carried rather than
// dropped, following this pipeline's rule that extracted data stays available even where
// nothing consumes it yet.
//
// Every list here is filtered to the chunk's own page range, so its cost scales with the
// chunk, not with the document. Measured per document on the 260812 corpus run: Headings
// 4.3 KB, Boilerplate 2.2 KB, Figures 1.2 KB. Tables is heavier
// (36.3 KB) but two indexed fields are derived from it.
//
// Lines are absent here AND on the document since 2026-09-09 (PdfDocumentStructure carries a
// LineCount). The list was 57% of the entire extraction payload by itself - 178 KB per
// document, a polygon per text line - so the polygon was dropped, and without it the list
// served no consumer; the highlight-on-source feature it was kept for would re-read line
// geometry off the response (CuGeometryHelper) the day it exists.
//
// Sections are absent for a different and stronger reason - see ChunkObject.
// They are per-DOCUMENT data, so attaching them here costs sections x chunk-count, and both
// factors peak on the same four documents. That is what took the chunks blob to 772 MB for
// 3,046 chunks and OOM'd EmbedAndUploadActivity on 260812.
//
// Deliberately NOT [JsonIgnore]'d on ChunkObject: that attribute is type-level, not
// call-site-level, so it would strip this from every serialization - including the
// ChunkActivity -> EmbedAndUploadActivity blob hand-off (chunks.json) and the Stage 2
// archive - silently losing the data before it could reach either. SearchUploadChunk is
// the Search-only projection instead, built right before the upload call.
public sealed record ChunkStructure(
    IReadOnlyList<Heading>           Headings,
    IReadOnlyList<Heading>           Boilerplate,
    IReadOnlyList<TableInfo>         Tables,
    PageDimensions?                  Dimensions,
    IReadOnlyList<FigureInfo>        Figures,
    // CU-typed additions (2026-08-26), page-filtered like everything above. Trailing defaults
    // so chunks blobs written before the fields existed still deserialize.
    IReadOnlyList<AnnotationInfo>?   Annotations = null,
    IReadOnlyList<HyperlinkInfo>?    Hyperlinks  = null)
{
    public static readonly ChunkStructure Empty = new([], [], [], null, [], [], []);
}
