using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Indexing.Csv.Services;

public interface IChunkingStrategy
{
    string Name { get; }
    IReadOnlyList<TextChunk> Chunk(string content);
}
