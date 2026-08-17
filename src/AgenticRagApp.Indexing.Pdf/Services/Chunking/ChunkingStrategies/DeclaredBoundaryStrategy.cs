using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Route 1: the document declared its units - honour them.
//
// Reached when HeadingSectionGate found a boundary worth honouring (>= 2 headings at >= 0.1 per
// 1,000 chars, or a Small document with >= 1). A declared boundary means something; a computed
// one is a hypothesis, which is route 2's business (RecursiveStrategy).
//
// NOT IMPLEMENTED YET - the skeleton is wired so the flow can be read end to end before any
// cutting logic moves. Step 3 of docs/2608/260818/chunking-service-refactor.md fills it in by
// moving SectionCascadeStrategy's logic here, unchanged, because this route is measured
// (1,273/1,273 headings located, 83-87% of sections never split) and those numbers have to stay
// comparable across the refactor:
//
//   1. blank Content -> ChunkingOutcome.Empty. No heading guard: heading absence is the gate's
//      business, and this class is only reached when the gate said yes.
//   2. HeadingLocator.Locate - preamble, paired zero-body merges and the N=1 case all fall out
//      of the same loop, which is why SingleSection never needed a class.
//   3. per section: price the prefix FIRST (titleLine + heading path), then
//      bodyCeiling = Max(512 - prefixTokens, 128), then split the body. The ceiling governs the
//      EMBEDDED text, so the carry-along is charged before the cut, not appended after.
//   4. ChunkingOutcome(units, HeadingsTotal, HeadingsLocated, PairedHeadingsMerged).
public sealed class DeclaredBoundaryStrategy : IDocumentChunkingStrategy
{
    public string Name => "DeclaredBoundary";

    public ChunkingOutcome Chunk(PdfExtractionDocument doc, string? domainTag = null) =>
        ChunkingOutcome.Empty;
}
