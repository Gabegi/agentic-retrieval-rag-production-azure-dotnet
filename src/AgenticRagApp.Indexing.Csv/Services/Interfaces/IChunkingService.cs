using AgenticRagApp.Indexing.Csv.Models;
using AgenticRagApp.Common.Models;
using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Indexing.Csv.Services;

public interface IChunkingService
{
    string Name { get; }

    // Low-level: splits raw text into TextChunks using the configured strategy.
    IReadOnlyList<TextChunk> Chunk(string content);

    // High-level: converts ExtractionDocuments into indexed ProtocolDocuments,
    // computes ChunkingResults, and emits all chunk telemetry.
    (IReadOnlyList<ChunkStatsSource> Docs, ChunkingResults Stats) ChunkDocuments(
        IReadOnlyList<ExtractionDocument> docs);
}
