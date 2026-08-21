namespace AgenticRagApp.Indexing.DI.Models;

// Everything extraction produced about the pages a chunk covers that Azure AI Search has no
// schema for - nested objects like TableInfo's cells. ChunkObject derives table_count,
// has_table and figure_captions from Tables/Figures; the rest is carried rather than
// dropped, following this pipeline's rule that extracted data stays available even where
// nothing consumes it yet.
//
// Every list here is filtered to the chunk's own page range, so its cost scales with the
// chunk, not with the document. Measured per document on the 260812 corpus run: Headings
// 4.3 KB, SelectionMarks 3.1 KB, Boilerplate 2.2 KB, Figures 1.2 KB. Tables is heavier
// (36.3 KB) but two indexed fields are derived from it.
//
// LINES IS DELIBERATELY ABSENT, and is the one exception to the rule above. It was 57% of
// the entire extraction payload by itself - 178 KB per document, a polygon per text line -
// and page-filtering only reduces it to roughly one copy per chunk covering that page. It
// exists for a future highlight-on-source feature, which would read it from
// PdfExtractionDocument (where it still lives, once) rather than from a chunk.
//
// Sections and Bookmarks are absent for a different and stronger reason - see ChunkObject.
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
    IReadOnlyList<SelectionMarkInfo> SelectionMarks,
    IReadOnlyList<FigureInfo>        Figures)
{
    public static readonly ChunkStructure Empty = new([], [], [], null, [], []);
}
