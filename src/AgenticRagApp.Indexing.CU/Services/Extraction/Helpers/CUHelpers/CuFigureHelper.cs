using System.Text.Json;
using Azure.AI.ContentUnderstanding;
using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

// Figures from the typed response - EVERY figure, chart and mermaid included: Caption, generated
// Description (enableFigureDescription, made explicit here), Id, Elements, Kind, Regions, Role,
// and the analysis Payload for the kinds that carry one.
//
// Payload used to be two more helpers (CuChartHelper / CuDiagramHelper) joined onto the figure
// by Id in the facade, under a "no element mapped twice" rule. Folded here 2026-09-09: the rule
// bought two files and a join for one property, and the corpus measured 2 charts and 2 mermaid
// diagrams in 688 figures (260827 file-facts) with nothing downstream reading Payload yet. One
// pass over Figures, one record per figure.
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

            // The SDK types the analysis output as a subclass per kind: DocumentChartFigure
            // (Content = Chart.js config, chartFormat=chartJs fixed on the prebuilt) and
            // DocumentMermaidFigure (Content = Mermaid source). The subclass IS the kind, so
            // both Kind and Payload are read off the runtime type - one switch, not two helpers.
            var (kind, payload) = f switch
            {
                DocumentChartFigure   c => ("chart",   c.Content is { Count: > 0 } ? Serialize(c.Content) : null),
                DocumentMermaidFigure m => ("mermaid", string.IsNullOrWhiteSpace(m.Content) ? null : m.Content),
                _                       => ((string?)null, (string?)null),
            };

            result.Add(new FigureInfo(
                Caption:     f.Caption?.Content,
                Offset:      offset,
                PageNumber:  CuPageHelper.PageAt(pageSpans, offset),
                Id:          f.Id,
                Elements:    [.. f.Elements ?? Enumerable.Empty<string>()],
                Description: f.Description,
                Kind:        kind,
                Payload:     payload,
                Regions:     regions,
                // The service's semantic role, distinct from Kind above (which is the runtime
                // subclass). Extensible enum, so ToString().
                Role:        f.Role?.ToString()));
        }

        // Aggregated for the same reason as the tables' - see CuTableHelper.
        if (unparseable > 0)
            warnings.Add(
                $"{unparseable} of {result.Count} figure(s) reported a geometry Source this build could not parse; their Regions are empty.");

        return result;
    }

    // The SDK hands a Chart.js config over as IDictionary<string, BinaryData> - each value
    // already raw JSON. Reassembled into ONE JSON object so FigureInfo.Payload is a single
    // renderable config rather than an SDK-shaped fragment. Going through JsonElement rather
    // than string concatenation means a value that is not valid JSON fails here, loudly, instead
    // of producing a payload nothing can parse.
    private static string Serialize(IDictionary<string, BinaryData> content) =>
        JsonSerializer.Serialize(content.ToDictionary(
            kv => kv.Key,
            kv => JsonSerializer.Deserialize<JsonElement>(kv.Value),
            StringComparer.Ordinal));
}
