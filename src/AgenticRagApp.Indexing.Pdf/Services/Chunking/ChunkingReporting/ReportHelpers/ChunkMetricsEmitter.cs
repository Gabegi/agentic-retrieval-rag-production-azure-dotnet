using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Observability;
using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Indexing.Pdf.Services;

// The chunking stage's OpenTelemetry counters. Same set, same tags and same order as
// CsvChunkingService.EmitChunkMetrics - the two stages report into one dashboard, so a
// divergence here reads as a pipeline difference rather than as what it would be.
//
// The PDF path lost these when EmitChunkMetrics was deleted with the old dispatch machinery,
// which is why chunk-size distribution and duplicate counts went blank for PDFs while CSV kept
// reporting them.
public static class ChunkMetricsEmitter
{
    public static void Emit(ChunkingStageMetrics stats, IReadOnlyList<ChunkObject> chunks)
    {
        var strategyTag = new KeyValuePair<string, object?>("strategy", stats.Strategy);

        Instrumentation.ChunksExtracted.Record(stats.ChunksProduced, strategyTag);

        // Per-chunk histogram - preserves the real distribution in App Insights, not just the
        // aggregates already in ChunkingStageMetrics.
        foreach (var chunk in chunks)
            Instrumentation.ChunkSizeChars.Record(chunk.Content.Length, strategyTag);

        Instrumentation.ChunkSizeBand.Add(stats.BandUnder100,  strategyTag, new("band", "under_100"));
        Instrumentation.ChunkSizeBand.Add(stats.Band100To500,  strategyTag, new("band", "100_to_500"));
        Instrumentation.ChunkSizeBand.Add(stats.Band500To1500, strategyTag, new("band", "500_to_1500"));
        Instrumentation.ChunkSizeBand.Add(stats.Band1500Plus,  strategyTag, new("band", "1500_plus"));

        Instrumentation.DuplicateChunks.Add(stats.DuplicateChunks,   strategyTag);
        Instrumentation.CoherentChunks.Add(stats.CoherentChunks,     strategyTag);
        Instrumentation.HeadingsDetected.Add(stats.HeadingsDetected, strategyTag);

        if (stats.DocsWithZeroChunks > 0)
            Instrumentation.DocsWithZeroChunks.Add(stats.DocsWithZeroChunks, strategyTag);
    }
}
