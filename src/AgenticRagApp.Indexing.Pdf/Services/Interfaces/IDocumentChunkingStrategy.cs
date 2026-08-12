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

// Axis 1 of the two-axis model: one strategy per document, selected by the three first-split
// decisions (see DocumentStrategySelector).
//
// Takes the whole document rather than a string, which the interface it replaces could not
// do - a heading-aware strategy needs the headings, the section tree, the page map and the
// routing measurements, none of which fit through Chunk(string).
public interface IDocumentChunkingStrategy
{
    string Name { get; }

    ChunkingOutcome Chunk(PdfExtractionDocument doc);
}
