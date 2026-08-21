using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using AgenticRagApp.Infrastructure.Clients.Embedding;
using AgenticRagApp.Infrastructure.Configuration;
using AgenticRagApp.Indexing.DI.Models;
using AgenticRagApp.Indexing.DI.Utils;
using AgenticRagApp.Observability;

namespace AgenticRagApp.Indexing.DI.Services;

public class EmbeddingService : IEmbeddingService
{
    private readonly IEmbeddingClient          _embeddingClient;
    private readonly IVectorCache              _vectorCache;
    private readonly IndexerConfig             _config;
    private readonly ILogger<EmbeddingService> _logger;

    private const int MaxParallelism = 4;
    private const int BatchSize = 100;    // one request per batch instead of one per chunk
    // A cheap pre-filter in the wrong unit - see EmbedBatchAsync. Kept because it costs a length
    // check where the token count costs a tokenizer pass, and it catches the pathological case
    // before that pass runs.
    private const int TruncationLimit = 24_000;

    // text-embedding-3-large's per-input limit. The real one, in the unit the model counts in.
    private const int MaxInputTokens = 8_191;

    // Cache reads/writes are just blob GETs/PUTs, not paid API calls - bounded concurrency
    // keeps them off the critical path without hammering the container. Same knob shape as
    // PdfExtractionOrchestrator.MaxExtractionParallelism.
    private const int MaxCacheParallelism = 8;

    public EmbeddingService(
        IEmbeddingClient                              embeddingClient,
        IVectorCache                                  vectorCache,
        IndexerConfig                                 config,
        ILogger<EmbeddingService>                     logger)
    {
        _embeddingClient = embeddingClient;
        _vectorCache     = vectorCache;
        _config          = config;
        _logger          = logger;
    }

    public async Task<EmbeddingRunResult> EmbedDocumentsAsync(
        IEnumerable<ChunkObject> documents,
        CancellationToken ct = default)
    {
        var docList = documents.ToList();

        // A chunk whose content hash is already cached gets its vector back for free - no
        // embedding API call. Only genuinely new/changed chunks (within an updated document,
        // typically just the pages that actually changed) go on to EmbedBatchAsync below.
        var (cached, toEmbed) = await SplitByCacheAsync(docList, ct);

        _logger.LogInformation(
            "Embedding {ToEmbed} of {Total} documents in batches of {BatchSize} ({CacheHits} reused from vector cache)",
            toEmbed.Count, docList.Count, BatchSize, cached.Count);

        var semaphore    = new SemaphoreSlim(MaxParallelism);
        var tasks        = toEmbed.Chunk(BatchSize).Select(batch => EmbedBatchAsync(batch, semaphore, ct)).ToList();
        var batchResults = await Task.WhenAll(tasks);
        var freshResults = batchResults.SelectMany(b => b.Results).ToArray();

        await CacheFreshVectorsAsync(freshResults, ct);

        _logger.LogInformation("Embedding complete — {Fresh} embedded, {Cached} reused", freshResults.Length, cached.Count);

        return new EmbeddingRunResult(
            Documents:        cached.Concat(freshResults.Select(r => r.Document)),
            ChunksTruncated:  freshResults.Count(r => r.Truncated),
            EmbeddingRetries: batchResults.Sum(b => b.Retries),
            VectorDimErrors:  freshResults.Count(r => r.DimError),
            CacheHits:        cached.Count);
    }

    // Splits by vector-cache hit/miss. A cached vector whose length no longer matches the
    // configured embedding dimensions (model/config changed since it was cached) is treated
    // as a miss rather than trusted blindly.
    private async Task<(List<ChunkObject> Cached, List<ChunkObject> ToEmbed)> SplitByCacheAsync(
        List<ChunkObject> docs, CancellationToken ct)
    {
        var cached  = new ConcurrentBag<ChunkObject>();
        var toEmbed = new ConcurrentBag<ChunkObject>();

        await Parallel.ForEachAsync(
            docs,
            new ParallelOptions { MaxDegreeOfParallelism = MaxCacheParallelism, CancellationToken = ct },
            async (doc, token) =>
            {
                var vector = await _vectorCache.TryGetAsync(doc.ContentHash, token);
                if (vector is { } v && v.Length == _config.OpenAiEmbeddingDimensions)
                {
                    doc.ContentVector = v;
                    cached.Add(doc);
                    Instrumentation.VectorCacheHits.Add(1);
                }
                else
                {
                    toEmbed.Add(doc);
                }
            });

        return (cached.ToList(), toEmbed.ToList());
    }

    // Writes every freshly-embedded chunk's vector back to the cache, keyed by content hash,
    // so the next run that touches an unchanged chunk with the same hash gets a cache hit
    // instead of paying to re-embed it. Skips dimension-mismatched vectors - not worth
    // caching a result we already know is wrong.
    private Task CacheFreshVectorsAsync(IReadOnlyList<EmbedChunkResult> results, CancellationToken ct) =>
        Parallel.ForEachAsync(
            results.Where(r => !r.DimError && r.Document.ContentVector is not null),
            new ParallelOptions { MaxDegreeOfParallelism = MaxCacheParallelism, CancellationToken = ct },
            (r, token) => new ValueTask(_vectorCache.SetAsync(r.Document.ContentHash, r.Document.ContentVector!, token)));

    private async Task<BatchResult> EmbedBatchAsync(
        IReadOnlyList<ChunkObject> batch, SemaphoreSlim semaphore, CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);
        try
        {
            var texts     = new string[batch.Count];
            var truncated = new bool[batch.Count];

            for (int i = 0; i < batch.Count; i++)
            {
                var text = batch[i].EmbeddingText;

                // Two guards, and only the second one measures the limit that actually exists.
                //
                // The character cut is a cheap pre-filter, not the rule: the model's limit is in
                // TOKENS, and chars-per-token is not a constant. Prose runs 3.10-3.28 chars/token
                // and table markdown 1.88-2.79 (TokenCounter's own measurements), so 24,000 chars
                // is ~7,300 tokens of prose but ~12,700 of table - the second is over the limit
                // and would have been truncated by the API instead of by us, silently, with the
                // count reported as untruncated.
                if (text.Length > TruncationLimit)
                {
                    _logger.LogWarning("Truncating oversized chunk {Id} ({Length} chars)", batch[i].Id, text.Length);
                    text = text[..TruncationLimit];
                    truncated[i] = true;
                    Instrumentation.ChunksTruncated.Add(1);
                }

                // The real count, on whatever survived the character cut - but only measured
                // when the text could possibly breach it: a BPE token consumes at least one
                // character, so length <= MaxInputTokens proves tokens <= MaxInputTokens
                // without a tokenizer pass. That keeps the pre-filter design honest (ordinary
                // ~512-token chunks never pay for counting) while staying exact - nothing that
                // could exceed the limit skips the count. This is the last point before the
                // text leaves for the API, and truncation past here is invisible.
                if (text.Length > MaxInputTokens)
                {
                    var tokens = TokenCounter.Count(text);
                    if (tokens > MaxInputTokens)
                    {
                        _logger.LogWarning(
                            "Chunk {Id} is {Tokens} tokens, over the {Limit}-token embedding input limit, in {Chars} chars — truncating on a token boundary",
                            batch[i].Id, tokens, MaxInputTokens, text.Length);

                        // Cut proportionally and re-measure rather than binary-searching the exact
                        // boundary: overshooting costs a few tokens of a chunk that should not exist,
                        // and a loop here would be complexity paid for a case measured at zero.
                        var keep = (int)(text.Length * (MaxInputTokens / (double)tokens));
                        while (keep > 0 && TokenCounter.Count(text[..keep]) > MaxInputTokens)
                            keep -= Math.Max(keep / 20, 1);

                        text = text[..Math.Max(keep, 0)];

                        if (!truncated[i])
                        {
                            truncated[i] = true;
                            Instrumentation.ChunksTruncated.Add(1);
                        }
                    }
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

    private record EmbedChunkResult(ChunkObject Document, bool Truncated, bool DimError);
    private record BatchResult(List<EmbedChunkResult> Results, int Retries);
}
