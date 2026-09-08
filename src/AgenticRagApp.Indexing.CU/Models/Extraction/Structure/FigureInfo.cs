namespace AgenticRagApp.Indexing.CU.Models;

// Id only matters if a caller ever fetches the actual cropped figure image via the
// figures output endpoint - Offset/Caption are enough for text-only consumers.
// Elements are the service's own JSON-pointer refs (e.g. "/paragraphs/12") into the
// paragraphs that discuss/describe this figure - broader than just its Caption.
//
// Description is Content Understanding's generated account of what the figure actually shows
// (enableFigureDescription), and it is the reason this pipeline moved off Document
// Intelligence. DI offered nothing equivalent, so PdfCleaner.ConvertFigure DELETED any figure
// with neither a <figcaption> nor an <img alt> - an uncaptioned diagram contributed nothing to
// the index at all. Trailing with a default so snapshots written before the field existed still
// deserialize (null = "this was extracted before descriptions existed", not "this figure has no
// description").
//
// Kind/Payload carry enableFigureAnalysis output (decision 2026-08-26: extend FigureInfo, no
// separate ChartInfo/DiagramInfo types). Kind is the service's DocumentFigureKind as a string
// ("chart" / "mermaid" / "unknown"); Payload is the analysis content for the kinds that carry
// one - Chart.js config JSON for a chart (CuChartHelper), Mermaid source for a diagram
// (CuDiagramHelper). Both trailing defaults for the same snapshot-compat reason as Description.
//
// Regions is the figure's geometry, parsed from CU's Source string by CuGeometryHelper
// (2026-09-08) - one region per page, the same shape and the same reason as TableInfo.Regions:
// this is what a crop or a highlight-on-source overlay reads, and re-acquiring it after the run
// means a paid re-analysis rather than a re-read. Null (not empty) for the same snapshot-compat
// reason as the three fields above: a chunks blob written before the field existed says
// "extracted before regions existed", which is not the same as "this figure has no geometry".
public sealed record FigureInfo(
    string? Caption,
    int? Offset,
    int PageNumber,
    string? Id,
    IReadOnlyList<string> Elements,
    string? Description = null,
    string? Kind = null,
    string? Payload = null,
    IReadOnlyList<DocumentRegion>? Regions = null);
