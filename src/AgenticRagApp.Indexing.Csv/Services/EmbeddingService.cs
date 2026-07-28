using Microsoft.Extensions.Logging;
using AgenticRagApp.Infrastructure.Clients.Embedding;
using AgenticRagApp.Infrastructure.Configuration;
using AgenticRagApp.Common.Models;
using AgenticRagApp.Observability;

namespace AgenticRagApp.Indexing.Csv.Services;

public class EmbeddingService : IEmbeddingService
{
    private readonly IEmbeddingClient          _embeddingClient;
    private readonly IndexerConfig             _config;
    private readonly ILogger<EmbeddingService> _logger;

    private const int MaxParallelism = 4;
    private const int BatchSize = 100;    // one request per batch instead of one per chunk
    private const int TruncationLimit = 24_000;

    public EmbeddingService(
        IEmbeddingClient                              embeddingClient,
        IndexerConfig                                 config,
        ILogger<EmbeddingService>                     logger)
    {
        _embeddingClient = embeddingClient;
        _config          = config;
        _logger          = logger;
    }

    public async Task<EmbeddingRunResult> EmbedDocumentsAsync(
        IEnumerable<ChunkStatsSource> documents,
        CancellationToken ct = default)
    {
        var docList   = documents.ToList();
        var semaphore = new SemaphoreSlim(MaxParallelism);

        _logger.LogInformation("Embedding {Count} documents in batches of {BatchSize}", docList.Count, BatchSize);

        var tasks        = docList.Chunk(BatchSize).Select(batch => EmbedBatchAsync(batch, semaphore, ct)).ToList();
        var batchResults = await Task.WhenAll(tasks);
        var results      = batchResults.SelectMany(b => b.Results).ToArray();

        _logger.LogInformation("Embedding complete — {Count} embedded", results.Length);

        return new EmbeddingRunResult(
            Documents:        results.Select(r => r.Document),
            ChunksTruncated:  results.Count(r => r.Truncated),
            EmbeddingRetries: batchResults.Sum(b => b.Retries),
            VectorDimErrors:  results.Count(r => r.DimError));
    }

    private async Task<BatchResult> EmbedBatchAsync(
        IReadOnlyList<ChunkStatsSource> batch, SemaphoreSlim semaphore, CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);
        try
        {
            var texts     = new string[batch.Count];
            var truncated = new bool[batch.Count];

            for (int i = 0; i < batch.Count; i++)
            {
                var text = batch[i].EmbeddingText;
                if (text.Length > TruncationLimit)
                {
                    _logger.LogWarning("Truncating oversized chunk {Id} ({Length} chars)", batch[i].Id, text.Length);
                    text = text[..TruncationLimit];
                    truncated[i] = true;
                    Instrumentation.ChunksTruncated.Add(1);
                }
                texts[i] = text;
            }

            var (vectors, retries) = await _embeddingClient.EmbedWithRetryAsync(texts, ct);
            if (retries > 0)
                Instrumentation.EmbeddingRetries.Add(retries);

            var results = new List<EmbedChunkResult>(batch.Count);
            for (int i = 0; i < batch.Count; i++)
            {
                var doc           = batch[i];
                doc.ContentVector = vectors[i];

                var dimError = doc.ContentVector?.Length != _config.OpenAiEmbeddingDimensions;
                if (dimError)
                {
                    _logger.LogError("Wrong vector dimensions {Dims} for {Id}", doc.ContentVector?.Length, doc.Id);
                    Instrumentation.VectorDimErrors.Add(1);
                }

                results.Add(new EmbedChunkResult(doc, truncated[i], dimError));
            }

            return new BatchResult(results, retries);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private record EmbedChunkResult(ChunkStatsSource Document, bool Truncated, bool DimError);
    private record BatchResult(List<EmbedChunkResult> Results, int Retries);
}
