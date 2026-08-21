namespace AgenticRagApp.Indexing.DI.Services;

// The two route names, in one place, for the same reason ChunkingBudget's ceiling is.
//
// A route name is not decoration: it is stamped onto every chunk as route_name, tags the
// per-chunk OpenTelemetry counters, decides the "chunked_unanchored" outcome, and is what
// ChunkingReporter counts the two routes with. Those four readers used to hold their own copy of
// the string literal, so renaming a strategy would have compiled cleanly and then silently:
// stopped producing chunked_unanchored, zeroed both route counts in the run summary, and split
// the metric tag away from the report - each one a number that quietly means something else
// rather than a failure anything reports.
public static class RouteNames
{
    // DeclaredBoundaryStrategy - the document's own headings became the section boundaries.
    public const string DeclaredBoundary = "DeclaredBoundary";

    // RecursiveStrategy - no usable declared structure, so the block cascade cut the whole
    // document flat.
    public const string Recursive = "Recursive";
}
