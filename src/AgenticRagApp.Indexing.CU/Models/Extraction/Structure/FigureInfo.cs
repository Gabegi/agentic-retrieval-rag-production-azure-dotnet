namespace AgenticRagApp.Indexing.CU.Models;

// Id only matters if a caller ever fetches the actual cropped figure image via the
// figures output endpoint - Offset/Caption are enough for text-only consumers.
// Elements are the service's own JSON-pointer refs (e.g. "/paragraphs/12") into the
// paragraphs that discuss/describe this figure - broader than just its Caption.
//
// Description is Content Understanding's generated account of what the figure actually shows,
// and it is the reason this pipeline moved off Document Intelligence. DI offered nothing
// equivalent, so PdfCleaner.ConvertFigure DELETED any figure with neither a <figcaption> nor an
// <img alt> - an uncaptioned diagram contributed nothing to the index at all. Trailing with a
// default so snapshots written before the field existed still deserialize (null = "this was
// extracted before descriptions existed", not "this figure has no description").
public sealed record FigureInfo(
    string? Caption,
    int? Offset,
    int PageNumber,
    string? Id,
    IReadOnlyList<string> Elements,
    string? Description = null);
