using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Indexing.Pdf.Utils;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Route 1's counterpart to FlatChunkBuilder: one section's pieces become ChunkObjects, carrying
// that section's heading.
//
// The two builders exist separately because the heading fields are not a variation on one shape
// - they are the difference between the routes. Route 2 has no heading and says so; route 1 has
// an anchored one and passes it through. A single builder taking nullable heading arguments
// would let a route 2 caller pass a heading it does not have, which is the failure mode both
// classes are written to prevent.
//
// Called once per section rather than once per document, so ChildIndex restarts inside each
// section: chunk identity is (SectionIndex, ChildIndex), and a running document-wide counter
// would renumber every chunk below an inserted section.
public static class SectionChunkBuilder
{
    // The prefix is prepended into Content because Content IS the embedded text
    // (ChunkObject.EmbeddingText), and it was priced against the ceiling before the cut for
    // exactly that reason - adding it afterwards would change every vector and force a full
    // re-embed. Same joiner as FlatChunkBuilder, so a document that changes route does not
    // silently re-embed differently.
    //
    // Start/Length address the BODY slice, not the composed string: they are cleaned-content
    // coordinates, and the prefix has no position in the document.
    public static IReadOnlyList<ChunkObject> Build(
        LocatedSection section, string prefix, IReadOnlyList<ContentPiece> pieces)
    {
        var chunks = new List<ChunkObject>(pieces.Count);

        for (var i = 0; i < pieces.Count; i++)
        {
            var piece = pieces[i];

            chunks.Add(new ChunkObject
            {
                Content = string.IsNullOrEmpty(prefix) ? piece.Text : $"{prefix}\n\n{piece.Text}",
                Start   = piece.Start,
                Length  = piece.Length,

                // One section, N children. A section that fit whole has exactly one child,
                // which is the 83-87% case - not a special shape, just N = 1.
                SectionIndex = section.Index,
                ChildIndex   = i,

                HeadingText  = section.HeadingText,
                HeadingPath  = section.HeadingPath,
                HeadingDepth = section.Depth,

                // Both come off the section rather than being asserted here. A preamble
                // section is a real route 1 section with HeadingSource None and no heading at
                // all, so stamping a flat `true` would produce the contradiction FlatChunkBuilder
                // exists to warn about: located true with source none reads as a successfully
                // anchored heading in any aggregate that counts one without reading the other.
                HeadingSource  = section.HeadingSource,
                HeadingLocated = section.Located && section.HeadingSource != ChunkHeadingSource.None,

                IsOverlap = piece.IsOverlap,
            });
        }

        return chunks;
    }
}
