namespace AgenticRagApp.Common.Models;

// What ChunkingStageMetrics.Compute needs from a chunk on top of the common IChunk shape.
// Implemented by each pipeline's own chunk type (e.g. ChunkObject) — Observability never
// references those types directly.
public interface IChunkStatsSource : IChunk
{
    bool IsCoherent { get; }
}
