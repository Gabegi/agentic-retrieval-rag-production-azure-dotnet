namespace AgenticRagApp.Indexing.DI.Models;

// Paired with PageDimensions for a future highlight-on-source feature: Offset is
// enough to join a line back to whichever chunk's span range contains it, and that
// chunk's lines' Polygons union into the highlight region - the join and the geometry
// already exist here, no re-analysis needed later to add this. See PageDimensions for
// why the polygon alone isn't renderable without it.
public sealed record LineInfo(string Content, int? Offset, int PageNumber, IReadOnlyList<PolygonPoint> Polygon);
