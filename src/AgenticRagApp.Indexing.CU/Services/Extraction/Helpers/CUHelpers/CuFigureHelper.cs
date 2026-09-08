using Azure.AI.ContentUnderstanding;
using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

// Figures from the typed response - the BASE fields of EVERY figure, chart and mermaid
// included: Caption, generated Description (enableFigureDescription, made explicit here), Id,
// Elements, Kind, Regions. The subclass payloads are NOT read here - CuChartHelper and
// CuDiagramHelper own those, keyed back to the same figure Id (the plan's ownership rule: no
// element mapped twice).
//
// Regions (2026-09-08) is the same geometry the tables get, off the same Source string and the
// same CuGeometryHelper - DocumentRegion's own comment always anticipated figures. It is what
// a crop or a citation highlight would need, and re-acquiring it later costs a paid
// re-analysis rather than a re-read.
internal static class CuFigureHelper
{
    internal static List<FigureInfo> Build(
        DocumentContent document, IReadOnlyList<PageSpan> pageSpans, List<string> warnings)
    {
        var result      = new List<FigureInfo>();
        var unparseable = 0;

        foreach (var f in document.Figures ?? Enumerable.Empty<DocumentFigure>())
        {
            var offset  = f.Span?.Offset;
            var regions = CuGeometryHelper.Parse(f.Source);

            if (CuGeometryHelper.FailedToParse(f.Source, regions))
                unparseable++;

            result.Add(new FigureInfo(
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
                Payload:     null,
                Regions:     regions));
        }

        // Aggregated for the same reason as the tables' - see CuTableHelper.
        if (unparseable > 0)
            warnings.Add(
                $"{unparseable} of {result.Count} figure(s) reported a geometry Source this build could not parse; their Regions are empty.");

        return result;
    }
}
