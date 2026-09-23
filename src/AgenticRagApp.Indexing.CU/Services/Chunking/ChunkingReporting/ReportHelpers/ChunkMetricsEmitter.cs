using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Observability;
using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Indexing.CU.Services;

// The chunking stage's OpenTelemetry counters. Same instruments, same "strategy" tag and same
// order the dashboards were built on - originally shared with the archived CSV pipeline's
// EmitChunkMetrics - so a divergence here reads as a pipeline difference rather than as what it
// would be.
//
// The PDF path lost these when EmitChunkMetrics was deleted with the old dispatch machinery,
// which is why chunk-size distribution and duplicate counts went blank for PDFs for a while.
//
// ONE addition over that original set, and it is additive on purpose: the per-chunk instruments
// carry a second "route" tag. Nothing that filters on "strategy" changes meaning or loses rows
// because of it - see Emit.
public static class ChunkMetricsEmitter
{
    // Every instrument keeps "strategy" = stats.Strategy, exactly as before. The per-chunk ones
    // gain a SECOND tag, "route".
    //
    // Two tags rather than overloading the first: stats.Strategy is "TwoAxisChunking" whichever
    // way a document went, so on its own it cannot answer "is route 2 producing worse chunks than
    // route 1" - the one question the two-strategy design exists to make askable. But replacing it
    // with the route would silently break every saved query and alert filtering
    // strategy == "TwoAxisChunking", and would change what the tag means from a pipeline to a
    // route. Adding a dimension costs nothing and takes neither away.
    //
    // The route is already on every chunk as Metadata.Route, so the split is a GroupBy and no
    // change to what ChunkActivity returns. A chunk with no route stamped (metadata never ran)
    // is tagged "unstamped" rather than dropping out of the counters or silently joining a real
    // route's bucket.
    //
    // The run-level counters take no route tag at all: DuplicateChunks and DocsWithZeroChunks are
    // computed across the whole run and cannot be attributed to a route without recomputing them
    // per route, which would change the stage's return shape.
    private const string UnstampedRoute = "unstamped";

    public static void Emit(ChunkingStageMetrics stats, IReadOnlyList<ChunkObject> chunks)
    {
        var strategyTag = new KeyValuePair<string, object?>("strategy", stats.Strategy);

        Instrumentation.ChunksExtracted.Record(stats.ChunksProduced, strategyTag);

        // Per-chunk histogram - preserves the real distribution in App Insights, not just the
        // aggregates already in ChunkingStageMetrics. Measured on StatsText for the reason
        // IChunkStatsSource gives: Content is the bare body, and a distribution that excludes
        // the prefix is not the distribution the 512 ceiling was priced against.
        foreach (var group in chunks.GroupBy(c => c.Metadata.Route ?? UnstampedRoute, StringComparer.Ordinal))
        {
            var routeTag = new KeyValuePair<string, object?>("route", group.Key);

            int band0 = 0, band1 = 0, band2 = 0, band3 = 0;
            int headings = 0;

            foreach (var chunk in group)
            {
                var len = chunk.StatsText.Length;
                Instrumentation.ChunkSizeChars.Record(len, strategyTag, routeTag);

                if      (len < 100)  band0++;
                else if (len < 500)  band1++;
                else if (len < 1500) band2++;
                else                 band3++;

                if (chunk.HeadingText != null) headings++;
            }

            Instrumentation.ChunkSizeBand.Add(band0, strategyTag, routeTag, new("band", "under_100"));
            Instrumentation.ChunkSizeBand.Add(band1, strategyTag, routeTag, new("band", "100_to_500"));
            Instrumentation.ChunkSizeBand.Add(band2, strategyTag, routeTag, new("band", "500_to_1500"));
            Instrumentation.ChunkSizeBand.Add(band3, strategyTag, routeTag, new("band", "1500_plus"));

            Instrumentation.HeadingsDetected.Add(headings, strategyTag, routeTag);

            // Cut level per chunk, every level including zeros (D224 A6) - the same counts the
            // report carries in CutBoundaries, per route here so the two routes can be compared.
            foreach (var (level, count) in CutBoundaryCounters.Of(group.ToList()).Buckets)
                Instrumentation.ChunkCutBoundaries.Add(count, strategyTag, routeTag, new("level", level));
        }

        Instrumentation.DuplicateChunks.Add(stats.DuplicateChunks, strategyTag);

        if (stats.ResidueChunksDropped > 0)
            Instrumentation.ResidueChunksDropped.Add(stats.ResidueChunksDropped, strategyTag);

        if (stats.TocChunksDropped > 0)
            Instrumentation.TocChunksDropped.Add(stats.TocChunksDropped, strategyTag);

        // The three diagram counts (D214 §2.8), same "only when non-zero" rule as the two above:
        // a run without diagrams emits nothing rather than a zero series.
        if (stats.DiagramBlocks > 0)
            Instrumentation.DiagramBlocks.Add(stats.DiagramBlocks, strategyTag);

        if (stats.DiagramBlocksCut > 0)
            Instrumentation.DiagramBlocksCut.Add(stats.DiagramBlocksCut, strategyTag);

        if (stats.DiagramFragmentsWithoutContext > 0)
            Instrumentation.DiagramFragmentsWithoutContext.Add(stats.DiagramFragmentsWithoutContext, strategyTag);

        if (stats.DocsWithZeroChunks > 0)
            Instrumentation.DocsWithZeroChunks.Add(stats.DocsWithZeroChunks, strategyTag);
    }
}
