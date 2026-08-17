using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// The default when no branch fits: picture documents (extraction gate failed, content likely
// in images) and structure-less documents. Chunks whatever text exists via the cascade until
// the Content Understanding branch lands (E6) - this class is where that enrichment plugs in,
// and the report row's SizeClass says which Fallbacks are CU candidates.
public sealed class FallbackStrategy : IDocumentChunkingStrategy
{
    private readonly SectionCascadeStrategy _cascade;

    public string Name => "Fallback";

    public FallbackStrategy(SectionCascadeStrategy cascade) => _cascade = cascade;

    public ChunkingOutcome Chunk(PdfExtractionDocument doc, string? domainTag = null) =>
        _cascade.Chunk(doc, domainTag);
}
