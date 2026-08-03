using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// PdfChunkingStrategy2 is the only implementation registered in DI (see
// ServiceCollectionExtensions.AddPdfIndexing).
public interface IChunkingStrategy
{
    string Name { get; }
    IReadOnlyList<TextChunk> Chunk(string content);
}
