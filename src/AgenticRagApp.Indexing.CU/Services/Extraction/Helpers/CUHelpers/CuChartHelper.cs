using System.Text;
using System.Text.Json;
using Azure.AI.ContentUnderstanding;
using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

// Chart payloads (enableFigureAnalysis): figures the service classified as charts arrive typed
// as DocumentChartFigure, whose Content is the Chart.js config - bar, line, pie, radar,
// scatter, bubble and quadrant charts, per the document-elements doc. chartFormat=chartJs is
// fixed on prebuilt-documentSearch, so Chart.js JSON is the only shape this ever sees.
//
// Owns ONLY the payload; the figure's base fields are CuFigureHelper's (ownership rule). The
// facade joins the two on figure Id, filling FigureInfo.Payload.
internal static class CuChartHelper
{
    internal static Dictionary<string, string> PayloadsOf(DocumentContent document) =>
        (document.Figures ?? Enumerable.Empty<DocumentFigure>())
            .OfType<DocumentChartFigure>()
            .Where(c => c.Id is not null && c.Content is { Count: > 0 })
            .ToDictionary(c => c.Id!, c => Serialize(c.Content));

    // The SDK types the Chart.js config as IDictionary<string, BinaryData> - each value already
    // raw JSON. Reassembled into one JSON object string so FigureInfo.Payload is a single
    // renderable Chart.js config, not an SDK-shaped fragment.
    private static string Serialize(IDictionary<string, BinaryData> content)
    {
        var sb = new StringBuilder("{");
        var first = true;
        foreach (var (key, value) in content)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(JsonSerializer.Serialize(key)).Append(':').Append(value.ToString());
        }
        return sb.Append('}').ToString();
    }
}
