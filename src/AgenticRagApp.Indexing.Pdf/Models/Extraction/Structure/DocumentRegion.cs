namespace AgenticRagApp.Indexing.Pdf.Models;

// A polygon paired with the page it's on - a bare polygon list is meaningless across
// pages, since page units reset per page (see PageDimensions). Shared shape for
// multi-region geometry (TableInfo today; figures once C fetches crops).
public sealed record DocumentRegion(int PageNumber, IReadOnlyList<PolygonPoint> Polygon);
