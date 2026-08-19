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
//
// Content is the BARE BODY - the embedded prefix is NOT prepended here, exactly as in
// FlatChunkBuilder. Step 4 stores it as ChunkMetadata.Prefix and ChunkObject.EmbeddingText
// composes the two; prepending here would put the prefix in the embedded text twice and break
// the slice invariant (Content == doc.Content[Start..(Start + Length)]) that page attribution,
// the snapshot round-trip and the minimum-content rule all read. The strategy still PRICES the
// prefix before cutting - that is what the ceiling is budgeted against.
public static class SectionChunkBuilder
{
    // Start/Length address the body slice in cleaned-content coordinates. The prefix has no
    // position in the document, which is the other reason it cannot live in Content.
    public static IReadOnlyList<ChunkObject> Build(
        LocatedSection section, IReadOnlyList<ContentPiece> pieces)
    {
        var chunks = new List<ChunkObject>(pieces.Count);

        for (var i = 0; i < pieces.Count; i++)
        {
            var piece = pieces[i];

            // A table that opens with a merged label row ("| Salarisschaal functiegroep 75 |"
            // repeated across its cells) names ITSELF, and that name is authoritative over
            // whatever heading the section inherited - it is part of the table, immune to the
            // caption drift a column-serialized page suffers. Without this, a section holding
            // several such tables stamps them all with its one heading (the CAO GHZ salary
            // appendix shape, 35 mislabelled chunks in the 260818 run). TableCutter repeats
            // header rows onto continuation fragments, so those carry the label too.
            var ownLabel = TableCaptionSplitter.MergedHeaderLabel(piece.Text);
            var relabel  = ownLabel is not null && ownLabel != section.HeadingText;

            chunks.Add(new ChunkObject
            {
                Content = piece.Text,
                Start   = piece.Start,
                Length  = piece.Length,

                // One section, N children. A section that fit whole has exactly one child,
                // which is the 83-87% case - not a special shape, just N = 1.
                SectionIndex = section.Index,
                ChildIndex   = i,

                HeadingText  = relabel ? ownLabel : section.HeadingText,
                HeadingPath  = relabel
                    ? (string.IsNullOrWhiteSpace(section.HeadingPath)
                          ? ownLabel
                          : $"{section.HeadingPath} > {ownLabel}")
                    : section.HeadingPath,
                HeadingDepth = section.Depth,

                // Both come off the section rather than being asserted here. A preamble
                // section is a real route 1 section with HeadingSource None and no heading at
                // all, so stamping a flat `true` would produce the contradiction FlatChunkBuilder
                // exists to warn about: located true with source none reads as a successfully
                // anchored heading in any aggregate that counts one without reading the other.
                // A relabelled chunk says so: its heading came from the table's own merged
                // header row, not from the section's signal.
                HeadingSource  = relabel ? ChunkHeadingSource.TableCaption : section.HeadingSource,
                HeadingLocated = relabel ||
                                 (section.Located && section.HeadingSource != ChunkHeadingSource.None),

                // Carried from the piece, not defaulted: BoundaryLevel is the fall-through
                // metric, and Degraded is the one flag that says an over-ceiling chunk was
                // deliberate. Both are None/false for a section that fit whole, which is the
                // honest value there - but they stop being so the moment the oversized path
                // below starts cutting.
                BoundaryLevel = piece.BoundaryLevel,
                Degraded      = piece.Degraded,
                IsOverlap     = piece.IsOverlap,
            });
        }

        return chunks;
    }
}
