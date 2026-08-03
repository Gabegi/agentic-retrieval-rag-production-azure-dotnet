namespace AgenticRagApp.Indexing.Pdf.Models;

// Paired with LineInfo for a future highlight-on-source feature (out of the embedding
// path, in the RAG system): DI's polygons are in page units (inches, for PDFs), so
// rendering an overlay box means normalizing LineInfo.Polygon against this page's
// Width/Height first - a raw polygon alone isn't renderable without it.
public sealed record PageDimensions(int PageNumber, double? Width, double? Height, string Unit);
