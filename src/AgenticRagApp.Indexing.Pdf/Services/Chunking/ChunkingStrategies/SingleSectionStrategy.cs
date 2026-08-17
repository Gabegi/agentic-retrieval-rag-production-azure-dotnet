using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Small document that stands as one section, earned by SizeClass Small + at least one
// heading. Delegates to the cascade, whose HeadingLocator already degrades to a single
// section covering the document; skipping the locator here would be a micro-optimization,
// only worth taking if it ever misfires on small documents.
public sealed class SingleSectionStrategy : IDocumentChunkingStrategy
{
    private readonly SectionCascadeStrategy _cascade;

    public string Name => "SingleSection";

    public SingleSectionStrategy(SectionCascadeStrategy cascade) => _cascade = cascade;

    public ChunkingOutcome Chunk(PdfExtractionDocument doc, string? domainTag = null) =>
        _cascade.Chunk(doc, domainTag);
}
