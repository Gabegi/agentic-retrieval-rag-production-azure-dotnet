using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Route 1: the document declared its units - honour them.
//
// Reached when the gate said yes (>= 2 headings at >= 0.1 per 1,000 chars, or a document under
// 4,000 tokens with >= 1). A declared boundary means something; a computed one is a hypothesis,
// which is route 2's business.
//
// An ORCHESTRATOR: every step is one call into StrategyHelpers. This class owns the ORDER of
// the steps and nothing else - no anchoring, no token arithmetic, no cutting.
public sealed class DeclaredBoundaryStrategy : IDocumentChunkingStrategy
{
    // Microsoft's starting point, and what the whole prefix-before-cut ordering is budgeted
    // against: the ceiling governs the EMBEDDED text, prefix included.
    private const int TokenCeiling = 512;

    // The floor the body keeps no matter how expensive the prefix got. A deep heading chain on
    // a long title can price the body down to nothing; below this the chunk stops being worth
    // retrieving, so the ceiling is breached by choice instead.
    private const int MinBodyTokenBudget = 128;

    public string Name => "DeclaredBoundary";

    public ValueTask<IReadOnlyList<ChunkObject>> ChunkDocumentAsync(
        PdfExtractionDocument doc, CancellationToken ct = default)
    {
        // Nothing to cut. Content is the only guard: a document with no headings is a routing
        // mistake, not this class's problem, and it still has text worth chunking.
        if (string.IsNullOrWhiteSpace(doc.Content))
            return ValueTask.FromResult<IReadOnlyList<ChunkObject>>([]);

        // 1. Read the sections. ChunkingService anchored them (HeadingLocator) before calling
        //    this route, so the whole read - sort by raw DI offset, find each heading's real
        //    position in the cleaned text, pair consecutive anchors, split off the preamble,
        //    merge paired zero-body headings - has already happened.
        //
        //    It sits up there rather than here because the three heading counters have to reach
        //    the run report even when this method goes on to emit nothing, and a strategy that
        //    returns only chunks cannot carry them out.
        //
        //    Empty is a routing mistake, not a defect here: the gate promised declared structure.
        //    Nothing to honour means nothing to cut.
        var sections = doc.LocatedSections ?? [];

        var chunks = new List<ChunkObject>();

        // 2. Per section: price the prefix, then decide.
        //
        //    Chunks are built PER SECTION rather than pooled and built once at the end, because
        //    SectionIndex is the section's own Index and ChildIndex counts within it - a document
        //    -wide piece list cannot say which section a piece came from, and that pair is the
        //    chunk's identity.
        foreach (var section in sections)
        {
            var body = doc.Content[section.Start..section.End];

            // 2a. Build the prefix: title line, sector tag, heading path capped to the last
            //     two or three levels. Capped on the PREFIX, not on the boundary - every
            //     heading still opens a section, only the embedded chain is truncated.
            //
            //     The sector tag rides in on the document (doc.Family), attached by
            //     ChunkingService from DocumentIdentityResolver's output. It is priced here,
            //     before the cut, because it goes inside the embedded text - adding it later
            //     would change every vector and force a full re-embed.
            var prefix = PrefixBuilder.Build(doc.Title, doc.Family?.DomainTag, section.HeadingPath);

            // 2b. Price it.
            var prefixTokens = TokenEstimator.Estimate(prefix);

            // 2c. What is left of the budget for the body.
            var bodyCeiling = Math.Max(TokenCeiling - prefixTokens, MinBodyTokenBudget);

            // 2d. Price the body.
            var bodyTokens = TokenEstimator.Estimate(body);

            // 3. The fit gate: does prefix + body fit under the ceiling?
            if (bodyTokens <= bodyCeiling)
            {
                // Fits - keep the section whole, one piece, no cut. This is the 83-87% path.
                // The piece carries only Text, Start, Length and BoundaryLevel; every other
                // field is assigned downstream.
                //
                // Start/Length are the SECTION's own, so the slice invariant holds by
                // construction: body was sliced at exactly these bounds.
                var whole = new ContentPiece(
                    Text:          body,
                    Start:         section.Start,
                    Length:        section.Length,
                    BoundaryLevel: BoundaryLevel.None);

                // 4. The cut becomes chunks, carrying this section's heading. The only place
                //    route 1's five heading fields are written.
                chunks.AddRange(SectionChunkBuilder.Build(section, [whole]));

                continue;
            }

            // Over the ceiling - TO DO. Nothing is emitted for such a section yet, so an
            // oversized section is currently DROPPED rather than split: the cutting cascade
            // route 2 runs (CutToCeiling) has no route-1 caller. Deliberate and temporary - see
            // the handoff doc - but it means this route's chunk count is a floor, not a total.
        }

        return ValueTask.FromResult<IReadOnlyList<ChunkObject>>(chunks);
    }
}
