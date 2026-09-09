namespace AgenticRagApp.Indexing.CU.Models;

// Page geometry for a future highlight-on-source feature (out of the embedding path, in the
// RAG system): CU reports polygons in page units (DocumentContent.Unit - inches for PDFs), so
// rendering an overlay box for a TableInfo/FigureInfo region means normalizing its polygon
// against this page Width/Height first - a raw polygon alone is not renderable without it.
// Rides on PageSpan.Dimensions; the parallel per-page list was removed 2026-09-09.
public sealed record PageDimensions(int PageNumber, double? Width, double? Height, string Unit);
