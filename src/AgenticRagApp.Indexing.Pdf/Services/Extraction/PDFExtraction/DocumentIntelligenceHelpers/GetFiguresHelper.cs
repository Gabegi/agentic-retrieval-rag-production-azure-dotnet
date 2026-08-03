using Azure.AI.DocumentIntelligence;
using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Every figure DI detected. All free under prebuilt-layout; no add-on required.
// - Id: only needed to fetch the cropped image later via the figures endpoint.
// - Elements: JSON-pointer refs to paragraphs discussing the figure, broader than Caption.
internal static class GetFiguresHelper
{
    public static IReadOnlyList<FigureInfo> GetFigures(AnalyzeResult result) =>
        (result.Figures ?? [])
            .Select(f => new FigureInfo(
                f.Caption?.Content,
                DiGeometryHelpers.FirstOffset(f.Spans),
                DiGeometryHelpers.FirstPage(f.BoundingRegions),
                f.Id,
                f.Elements ?? []))
            .ToList();
}
