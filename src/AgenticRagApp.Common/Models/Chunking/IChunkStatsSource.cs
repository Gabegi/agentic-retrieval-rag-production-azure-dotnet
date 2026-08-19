namespace AgenticRagApp.Common.Models;

// What ChunkingStageMetrics.Compute needs from a chunk on top of the common IChunk shape.
// Implemented by each pipeline's own chunk type (e.g. ChunkObject) — Observability never
// references those types directly.
public interface IChunkStatsSource : IChunk
{
    bool IsCoherent { get; }

    // The string the size bands, the size extremes and duplicate detection are measured on.
    //
    // BOTH pipelines override it with their EmbeddingText, because both separate the stored body
    // from the text they actually embed - PDF holds the prefix beside Content, CSV holds the
    // summary beside it. Measured on Content, a size band excludes text that reaches the embedder
    // anyway, and two chunks with identical bodies under different prefixes/summaries count as
    // duplicates despite producing different vectors.
    //
    // It defaults to Content rather than being abstract so that a new chunk type with no such
    // split needs no override, and gets the only sensible answer. If you add a type WITH a split,
    // override it: an inherited default here is silent, and the number it produces is wrong in a
    // way nothing reports.
    //
    // NOT the same decision as IsCoherent, which stays on the bare body deliberately - see
    // ChunkObject.
    string StatsText => Content;
}
