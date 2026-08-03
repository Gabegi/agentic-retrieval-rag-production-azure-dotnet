namespace AgenticRagApp.Indexing.Pdf.Models;

// Confidence/Polygon come straight off the same DocumentSelectionMark GetSelectionMarks
// already iterates for State/Offset - free fields on an object already in hand.
public sealed record SelectionMarkInfo(int PageNumber, string State, int Offset, double Confidence, IReadOnlyList<PolygonPoint> Polygon);
