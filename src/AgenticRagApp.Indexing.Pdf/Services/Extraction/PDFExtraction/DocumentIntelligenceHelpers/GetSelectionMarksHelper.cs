using Azure.AI.DocumentIntelligence;
using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Every checkbox/radio: state, DI's confidence, bounding polygon.
// Offset comes from Span (singular): a selection mark has exactly one position,
// unlike paragraphs/tables.
internal static class GetSelectionMarksHelper
{
    public static IReadOnlyList<SelectionMarkInfo> GetSelectionMarks(AnalyzeResult result) =>
        result.Pages
            .SelectMany(p => (p.SelectionMarks ?? []).Select(sm => new SelectionMarkInfo(
                p.PageNumber, sm.State.ToString(), sm.Span.Offset, sm.Confidence,
                DiGeometryHelpers.ToPolygonPoints(sm.Polygon))))
            .ToList();
}
