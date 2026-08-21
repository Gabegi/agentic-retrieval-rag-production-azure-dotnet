namespace AgenticRagApp.Indexing.CU.Models;

// A single point in page units. See PageDimensions for why page units aren't renderable
// on their own - a polygon has to be normalized against its page's Width/Height first.
public sealed record PolygonPoint(float X, float Y);
