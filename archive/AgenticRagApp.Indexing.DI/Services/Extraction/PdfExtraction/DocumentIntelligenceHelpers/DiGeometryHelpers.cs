using Azure.AI.DocumentIntelligence;
using AgenticRagApp.Indexing.DI.Models;

namespace AgenticRagApp.Indexing.DI.Services;

// Small shared building blocks for turning DI's raw span/region/polygon data into this
// codebase's own position types (Offset/PageNumber/PolygonPoint). Used only by the
// Get*Helper classes in this folder - nothing in PdfDocumentIntelligenceAnalyzer itself
// calls these anymore.
internal static class DiGeometryHelpers
{
    public static Heading ToHeading(DocumentParagraph p) => new(
        p.Content,
        p.Role.ToString()!,
        FirstOffset(p.Spans),
        FirstPage(p.BoundingRegions));

    // Offset is null, never 0, when there are no spans: 0 is a valid real offset and
    // can't double as "unknown".
    public static int? FirstOffset(IReadOnlyList<DocumentSpan>? spans) =>
        spans is { Count: > 0 } s ? s[0].Offset : null;

    public static int FirstPage(IReadOnlyList<BoundingRegion>? regions) =>
        regions is { Count: > 0 } r ? r[0].PageNumber : 0;

    // DI returns polygons as a flat [x1, y1, x2, y2, ...] float list rather than typed
    // points; paired up here so callers don't have to know that.
    public static IReadOnlyList<PolygonPoint> ToPolygonPoints(IReadOnlyList<float>? polygon)
    {
        if (polygon is not { Count: > 1 }) return [];

        var points = new List<PolygonPoint>(polygon.Count / 2);
        for (var i = 0; i + 1 < polygon.Count; i += 2)
            points.Add(new PolygonPoint(polygon[i], polygon[i + 1]));
        return points;
    }
}
