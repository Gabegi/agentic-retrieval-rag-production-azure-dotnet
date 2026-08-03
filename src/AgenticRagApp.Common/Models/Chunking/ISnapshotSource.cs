namespace AgenticRagApp.Common.Models;

// What SnapshotChunk.From needs from a chunk on top of the common IChunk shape.
// Implemented by each pipeline's own chunk type (e.g. DocumentChunk) — Observability never
// references those types directly.
//
// ContentHash is what lets a restore resolve a vector from the vector cache instead of
// paying to re-embed, so it is required here even though nothing else reads it.
public interface ISnapshotSource : IChunk
{
    string?         Title            { get; }
    DateTimeOffset? LastModifiedDate { get; }
    string          ContentHash      { get; }
}
