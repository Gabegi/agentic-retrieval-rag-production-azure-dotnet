using Azure.AI.ContentUnderstanding;
using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

// Figures from the typed response - the BASE fields of EVERY figure, chart and mermaid
// included: Caption, generated Description (enableFigureDescription, made explicit here), Id,
// Elements, Kind. The subclass payloads are NOT read here - CuChartHelper and CuDiagramHelper
// own those, keyed back to the same figure Id (the plan's ownership rule: no element mapped
// twice).
internal static class CuFigureHelper
{
    internal static List<FigureInfo> Build(DocumentContent document, IReadOnlyList<PageSpan> pageSpans) =>
        [.. (document.Figures ?? Enumerable.Empty<DocumentFigure>())
            .Select(f =>
            {
                var offset = f.Span?.Offset;
                return new FigureInfo(
                    Caption:     f.Caption?.Content,
                    Offset:      offset,
                    PageNumber:  CuPageHelper.PageAt(pageSpans, offset),
                    Id:          f.Id,
                    Elements:    [.. f.Elements ?? Enumerable.Empty<string>()],
                    Description: f.Description,
                    // The base figure's Kind property is not public on this SDK build; the
                    // subclass IS the kind, so it is read off the runtime type instead.
                    Kind:        f switch
                    {
                        DocumentChartFigure   => "chart",
                        DocumentMermaidFigure => "mermaid",
                        _                     => null,
                    },
                    Payload:     null);
            })];
}
