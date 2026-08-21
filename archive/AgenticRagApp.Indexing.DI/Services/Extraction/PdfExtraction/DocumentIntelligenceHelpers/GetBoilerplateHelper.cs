using Azure.AI.DocumentIntelligence;
using AgenticRagApp.Indexing.DI.Models;

namespace AgenticRagApp.Indexing.DI.Services;

// Repeated page furniture, kept separate so "Headings" only ever means real structure.
// PageNumber belongs here rather than in its own bucket; without it those paragraphs
// fell through both and vanished.
internal static class GetBoilerplateHelper
{
    public static IReadOnlyList<Heading> GetBoilerplate(AnalyzeResult result) =>
        (result.Paragraphs ?? [])
            .Where(p => p.Role == ParagraphRole.PageHeader || p.Role == ParagraphRole.PageFooter
                     || p.Role == ParagraphRole.Footnote   || p.Role == ParagraphRole.PageNumber)
            .Select(DiGeometryHelpers.ToHeading)
            .ToList();
}
