using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

public interface IChunkingStrategy
{
    string Name { get; }
    IReadOnlyList<TextChunk> Chunk(string content);
}
