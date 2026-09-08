using System.Drawing;
using Azure.AI.ContentUnderstanding;
using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

// Element geometry, off the one part of the response CU encodes as a string instead of typed
// points: Source, "D(page,x1,y1,...,x4,y4)", with a ';'-separated segment per page for an
// element that spans a page break (SDK 1.1.0, DocumentSource remarks).
//
// THE SDK OWNS THE PARSE. DocumentSource.Parse is the service's own decoder for its own
// encoding, and it is public on 1.1.0 - so nothing here splits strings or counts commas. That
// also settles what a second reader of the format would have had to guess: how many points a
// polygon has (three or more, not necessarily four), and what "D(1)" means (a page with no
// coordinates at all, which the SDK reports as PageNumber with a null Polygon).
//
// Map-what's-there, same as the rest of the mapper: an absent Source and an unparseable one
// both yield no regions, and the CALLER counts and warns - it is the one that knows whether
// it was reading a table or a figure and how many of them came back without geometry.
internal static class CuGeometryHelper
{
    // Absent Source -> no regions. Unparseable Source -> no regions (the caller distinguishes
    // the two: a non-empty Source that returns nothing is a parse failure worth a warning).
    //
    // A "D(page)" segment with no coordinates still yields a region: the page attribution is
    // real data, and it is exactly what a cross-page table needs (see A10 in
    // docs/2609/260908/cu-payload-additions-action-plan.md). Its Polygon is empty rather than
    // fabricated, so a highlight consumer must check it before rendering - the same contract
    // LineInfo.Polygon already has.
    internal static IReadOnlyList<DocumentRegion> Parse(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return [];

        DocumentSource[] parsed;
        try
        {
            parsed = DocumentSource.Parse(source);
        }
        catch (FormatException)
        {
            // Never fails the file: a mis-encoded geometry string costs the region, not the
            // document. The caller turns the empty result into one aggregate warning.
            return [];
        }

        return [.. parsed.Select(s => new DocumentRegion(
            s.PageNumber,
            [.. (s.Polygon ?? Enumerable.Empty<PointF>()).Select(p => new PolygonPoint(p.X, p.Y))]))];
    }

    // True when a Source was reported but produced no regions - i.e. the service sent geometry
    // this build could not read. Absent Source is not malformed.
    internal static bool FailedToParse(string? source, IReadOnlyList<DocumentRegion> regions) =>
        !string.IsNullOrWhiteSpace(source) && regions.Count == 0;
}
