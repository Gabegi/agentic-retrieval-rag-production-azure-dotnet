using Azure.AI.ContentUnderstanding;

namespace AgenticRagApp.Indexing.CU.Services;

// Diagram payloads (enableFigureAnalysis): figures the service classified as diagrams arrive
// typed as DocumentMermaidFigure, whose Content is Mermaid source - mixed charts, flow charts,
// sequence diagrams and Gantt charts, per the document-elements doc.
//
// Owns ONLY the payload; the figure's base fields are CuFigureHelper's (ownership rule). The
// facade joins the two on figure Id, filling FigureInfo.Payload.
internal static class CuDiagramHelper
{
    internal static Dictionary<string, string> PayloadsOf(DocumentContent document) =>
        (document.Figures ?? Enumerable.Empty<DocumentFigure>())
            .OfType<DocumentMermaidFigure>()
            .Where(m => m.Id is not null && !string.IsNullOrWhiteSpace(m.Content))
            .ToDictionary(m => m.Id!, m => m.Content!);
}
