namespace AgenticRagApp.Common.Models;

// The identity and content every indexed chunk has, regardless of which doc-type pipeline
// produced it. Extracted because IChunkStatsSource and ISnapshotSource each declared
// DocumentId/Content/Heading separately, so the fact that they describe the same chunk
// was visible only in their (near-identical) comments and not in the type system.
//
// Observability depends on these interfaces rather than on DocumentChunk, so it never
// references a pipeline's own chunk type.
public interface IChunk
{
    string  Id         { get; }
    string  DocumentId { get; }
    string  Content    { get; }
    string? Heading    { get; }
    int     PageNumber { get; }
    int     ChunkIndex { get; }
}
