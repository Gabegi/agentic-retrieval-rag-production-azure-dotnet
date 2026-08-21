using Azure.AI.DocumentIntelligence;
using AgenticRagApp.Indexing.DI.Models;

namespace AgenticRagApp.Indexing.DI.Services;

// Every OCR line with its polygon: the most granular positional data DI offers free.
// - Future highlight-on-source join: a chunk's span range selects its lines by
//   Offset, and their polygons union into the highlight region.
// - By far the bulkiest structure here. Not persisted permanently today (dev reports
//   only), which is correct until source-grounding ships.
internal static class GetLinesHelper
{
    public static IReadOnlyList<LineInfo> GetLines(AnalyzeResult result) =>
        result.Pages
            .SelectMany(p => (p.Lines ?? []).Select(line => new LineInfo(
                line.Content, DiGeometryHelpers.FirstOffset(line.Spans), p.PageNumber,
                DiGeometryHelpers.ToPolygonPoints(line.Polygon))))
            .ToList();
}
