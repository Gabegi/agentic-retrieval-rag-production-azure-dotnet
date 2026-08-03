using Azure.AI.DocumentIntelligence;
using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Paragraphs DI classified as real section structure, not incidental roles.
// Offset/PageNumber come from Spans/BoundingRegions: DocumentParagraph has no
// PageNumber of its own.
internal static class GetHeadingsHelper
{
    public static IReadOnlyList<Heading> GetHeadings(AnalyzeResult result) =>
        (result.Paragraphs ?? [])
            .Where(p => p.Role == ParagraphRole.Title || p.Role == ParagraphRole.SectionHeading)
            .Select(DiGeometryHelpers.ToHeading)
            .ToList();
}
