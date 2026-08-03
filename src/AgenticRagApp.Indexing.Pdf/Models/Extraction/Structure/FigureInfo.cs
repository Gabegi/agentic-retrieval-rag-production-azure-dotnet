namespace AgenticRagApp.Indexing.Pdf.Models;

// Id only matters if a caller ever fetches the actual cropped figure image via the
// figures output endpoint - Offset/Caption are enough for text-only consumers.
// Elements are DI's own JSON-pointer refs (e.g. "/paragraphs/12") into the paragraphs
// that discuss/describe this figure - broader than just its Caption.
public sealed record FigureInfo(string? Caption, int? Offset, int PageNumber, string? Id, IReadOnlyList<string> Elements);
