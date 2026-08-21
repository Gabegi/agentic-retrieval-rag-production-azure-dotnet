using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

// Step 8 of the recursive route: pieces become ChunkObjects, with the degenerate heading
// constants this route always carries.
//
// FLAT by definition. There is one section - the document - so SectionIndex is 0 on every chunk
// and ChildIndex just counts. Nothing here is a placeholder for heading data that might arrive
// later: this route never anchors a heading, and a chunk that pretended otherwise would inflate
// every heading-coverage aggregate the run report produces.
//
// HeadingLocated is FALSE with HeadingSource "none", never true. The strategy this replaces set
// true with source none, which reads as a successfully located heading in any aggregate that
// counts one without reading the other. Never reproduce it.
//
// Content is the BARE BODY - the embedded prefix is NOT prepended here. Step 4 stores it as
// ChunkMetadata.Prefix and ChunkObject.EmbeddingText composes the two, so prepending here as
// well would embed the title and sector tag twice. It would also break the invariant that a
// chunk's Content is exactly its own slice of the source, which page attribution, the offset
// round-trip and the minimum-content rule all read. The strategy still PRICES the prefix before
// cutting - that is what the ceiling is budgeted against - it just does not carry it.
public static class FlatChunkBuilder
{
    // Start/Length address the body slice in cleaned-content coordinates. The prefix has no
    // position in the document, which is the other reason it cannot live in Content.
    public static IReadOnlyList<ChunkObject> Build(IReadOnlyList<ContentPiece> pieces)
    {
        var chunks = new List<ChunkObject>(pieces.Count);

        for (var i = 0; i < pieces.Count; i++)
        {
            var piece = pieces[i];

            chunks.Add(new ChunkObject
            {
                Content = piece.Text,
                Start   = piece.Start,
                Length  = piece.Length,

                // One section, N children.
                SectionIndex = 0,
                ChildIndex   = i,

                // No heading exists on this route - not a missing one, an absent one.
                HeadingText    = null,
                HeadingPath    = null,
                HeadingDepth   = 0,
                HeadingSource  = ChunkHeadingSource.None,
                HeadingLocated = false,

                BoundaryLevel = piece.BoundaryLevel,
                Degraded      = piece.Degraded,
                IsOverlap     = piece.IsOverlap,
            });
        }

        return chunks;
    }
}
