using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Cut at validated heading boundaries - the primary branch, earned by usable sections
// (SectionChecker). This route IS the section cascade, so the delegation below is its whole
// implementation; the class exists so every ChunkingStrategyKind has exactly one
// implementation to dispatch to.
public sealed class HeadingBasedStrategy : IDocumentChunkingStrategy
{
    private readonly SectionCascadeStrategy _cascade;

    public string Name => "HeadingBased";

    public HeadingBasedStrategy(SectionCascadeStrategy cascade) => _cascade = cascade;

    public ChunkingOutcome Chunk(PdfExtractionDocument doc, string? domainTag = null) =>
        _cascade.Chunk(doc, domainTag);
}
