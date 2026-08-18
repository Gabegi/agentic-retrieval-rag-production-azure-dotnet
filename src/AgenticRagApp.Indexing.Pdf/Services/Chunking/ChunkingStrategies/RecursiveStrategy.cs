using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Route 2: nothing trustworthy was declared, so compute a hypothesis.
//
// Reached when HeadingSectionGate said no. Flat by design: one section, N children, no heading
// machinery at all. An empty heading list is NORMAL input here, not a defect - it is this
// route's whole premise.
//
// An ORCHESTRATOR, same contract as DeclaredBoundaryStrategy: every step is one call into
// StrategyHelpers. This class owns the ORDER of the steps and nothing else - no parsing, no
// token arithmetic, no cutting.
//
// The cutting itself is BlockCascade's, shared with route 1's oversized sections: a block is
// classified once, by the strongest structure it shows, and that decides how it may be cut -
// tables on rows, key-value runs on pairs, lists on items, prose last. What is left here is the
// part that is genuinely this route's own: the whole document is the window, and the title line
// is the only context a chunk gets.
public sealed class RecursiveStrategy : IDocumentChunkingStrategy
{
    // The ceiling governs the EMBEDDED text, prefix included - same budget as route 1.
    private const int TokenCeiling = 512;

    // The floor the body keeps no matter how expensive the prefix got.
    private const int MinBodyTokenBudget = 128;

    public string Name => "Recursive";

    public ValueTask<IReadOnlyList<ChunkObject>> ChunkDocumentAsync(
        PdfExtractionDocument doc, CancellationToken ct = default)
    {
        // 1a. Nothing to cut. Content is the only guard on this route - see the class note.
        if (string.IsNullOrWhiteSpace(doc.Content))
            return ValueTask.FromResult<IReadOnlyList<ChunkObject>>([]);

        // 1b. Price the prefix. The title line plus the sector tag is the ONLY context a chunk
        //     on this route carries - there is no heading path to add - which is why an empty
        //     or oversized title is worth reporting rather than absorbing.
        var prefix       = PrefixBuilder.Build(doc.Title, doc.Family?.DomainTag, headingPath: null);
        var prefixTokens = TokenEstimator.Estimate(prefix);

        // 1c. A prefix that costs more than the body's own floor is not context any more, it is
        //     the chunk. Bail rather than emit chunks that are mostly title.
        if (prefixTokens > MinBodyTokenBudget)
            return ValueTask.FromResult<IReadOnlyList<ChunkObject>>([]);

        // What is left of the budget for the body, once the prefix is paid for.
        var bodyCeiling = Math.Max(TokenCeiling - prefixTokens, MinBodyTokenBudget);

        // 2-7. Cut. The whole document is the window, because this route's "section" IS the
        //      document - that is the guaranteed form of the sparse-giant hazard, and the
        //      reason nothing here narrows the range first.
        //
        //      The cascade itself (block parse, the three atomic kinds, prose packing, then
        //      the line -> sentence -> word -> hard ladder) lives in BlockCascade, shared with
        //      route 1's oversized sections. It moved there unchanged: same order, same
        //      ceiling, same pieces.
        var pieces = BlockCascade.Cut(doc.Content, 0, doc.Content.Length, bodyCeiling);

        // 8. One ChunkObject per piece: SectionIndex 0, running ChildIndex, heading fields null,
        //    HeadingSource "none", HeadingLocated FALSE. True with source "none" is a
        //    contradiction - it reads as a successful location in any aggregate.
        return ValueTask.FromResult(FlatChunkBuilder.Build(doc, prefix, pieces));
    }
}
