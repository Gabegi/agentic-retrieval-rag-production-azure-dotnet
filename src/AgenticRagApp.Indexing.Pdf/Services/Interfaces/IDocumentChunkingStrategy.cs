using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Everything one strategy produced for one document, plus what it observed while doing it.
//
// The diagnostics are not decoration. The heading-location failure rate is the standing
// evidence for the decision to locate headings by string match rather than rewrite PdfCleaner
// to emit an offset map: that call was made against a measured 1,273/1,273 exact matches, with
// an escalation threshold fixed in advance at >2%. If this metric moves, the decision is due
// to be reopened - which only works if it is reported every run rather than measured once.
public sealed record ChunkingOutcome(
    IReadOnlyList<ChunkUnit> Units,
    int HeadingsTotal,
    int HeadingsLocated,
    int PairedHeadingsMerged)
{
    public static readonly ChunkingOutcome Empty = new([], 0, 0, 0);
}

// Axis 1 of the two-axis model: one strategy per document. ChunkingStrategySelector decides
// each document's route; ChunkingService dispatches the decision onto an implementation.
//
// Takes the whole document rather than a string, which the interface it replaces could not
// do - a heading-aware strategy needs the headings, the section tree, the page map and the
// routing measurements, none of which fit through Chunk(string).
public interface IDocumentChunkingStrategy
{
    string Name { get; }

    // domainTag is the document's resolved sector tag (family identity), passed in because it
    // ends up inside the embedded prefix ChunkingService prepends to every chunk - and the
    // token ceiling governs the WHOLE embedded text, so the strategy has to know the prefix's
    // size before it cuts. Without it, every chunk exceeded its own ceiling by the length of
    // its prefix, up to ~220 tokens on deep heading chains (first-run-findings.md §2).
    ChunkingOutcome Chunk(PdfExtractionDocument doc, string? domainTag = null);
}
