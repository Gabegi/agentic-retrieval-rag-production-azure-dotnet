namespace AgenticRagApp.Indexing.Pdf.Models;

// Everything extraction produced about one chunk's page that Azure AI Search has no
// schema for - nested objects like TableInfo's cells, or geometry like LineInfo's
// polygons. Grouped into one named thing rather than seven loose properties on
// DocumentChunk, where the reason they were carried but not indexed needed a paragraph
// of comment to explain.
//
// Deliberately NOT [JsonIgnore]'d on DocumentChunk: that attribute is type-level, not
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
    IReadOnlyList<FigureInfo>        Figures,
    IReadOnlyList<LineInfo>          Lines)
{
    public static readonly ChunkStructure Empty = new([], [], [], null, [], [], []);
}
