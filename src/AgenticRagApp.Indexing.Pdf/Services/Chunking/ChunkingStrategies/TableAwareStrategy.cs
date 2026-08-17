using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Table-shaped document, earned by table dominance (TableCharShare). Delegates to the
// cascade - and probably always will: tables are an atomicity constraint inside its splitter
// (no-cut region, row-split with the header repeated per fragment), not a different cutter.
// The route exists so the run report can say which documents were picked for their tables.
public sealed class TableAwareStrategy : IDocumentChunkingStrategy
{
    private readonly SectionCascadeStrategy _cascade;

    public string Name => "TableAware";

    public TableAwareStrategy(SectionCascadeStrategy cascade) => _cascade = cascade;

    public ChunkingOutcome Chunk(PdfExtractionDocument doc, string? domainTag = null) =>
        _cascade.Chunk(doc, domainTag);
}
