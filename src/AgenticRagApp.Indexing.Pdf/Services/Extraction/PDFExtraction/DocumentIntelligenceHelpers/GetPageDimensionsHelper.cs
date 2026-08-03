using Azure.AI.DocumentIntelligence;
using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Each page's width/height/unit as DI measured it, not the PDF's own MediaBox.
internal static class GetPageDimensionsHelper
{
    public static IReadOnlyList<PageDimensions> GetPageDimensions(AnalyzeResult result) =>
        result.Pages
            .Select(p => new PageDimensions(p.PageNumber, p.Width, p.Height, p.Unit.ToString() ?? ""))
            .ToList();
}
