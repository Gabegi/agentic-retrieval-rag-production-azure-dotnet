namespace AgenticRagApp.Common.Models;

// The identity and content every indexed chunk has, regardless of which doc-type pipeline
// produced it. Extracted because IChunkStatsSource and ISnapshotSource each declared
// DocumentId/Content/Heading separately, so the fact that they describe the same chunk
// was visible only in their (near-identical) comments and not in the type system.
//
// Observability depends on these interfaces rather than on DocumentChunk, so it never
// references a pipeline's own chunk type.
//
// Naming follows action-plan.md §4.6: *_id names a thing, *_index names a position within
// an explicitly stated scope. The old names are kept out deliberately - "Heading",
// "PageNumber" and "ChunkIndex" were three of the four places the same word meant a
// different scope, and leaving them here would have kept that vocabulary alive in Common
// while the pipeline moved off it.
public interface IChunk
{
    string  Id          { get; }
    string  DocumentId  { get; }
    string  Content     { get; }

    // The unit's own heading, leaf only - not the chain.
    string? HeadingText { get; }

    // First page the unit starts on. A unit can span pages once sections are the grain.
    int     PageStart   { get; }

    // Position of this child within its section (was position within its page).
    int     ChildIndex  { get; }
}
