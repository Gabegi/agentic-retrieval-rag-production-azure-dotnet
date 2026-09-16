using Microsoft.Extensions.Logging;
using AgenticRagApp.Infrastructure.Clients.Embedding;
using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Utils;
using AgenticRagApp.Observability;

namespace AgenticRagApp.Indexing.CU.Services;

// One embedding API call's worth of work: truncate to the model's input limit, send, then
// validate what comes back. Split out of EmbeddingService (2026-09-16) so that class is the
// run-level orchestration (cache split, batching, clocks, run totals) and this one is the
// per-batch rules - the two change for different reasons, the batch rules being the ones tied
// to a specific model's limits.
//
// Constructed by EmbeddingService rather than registered in DI, and handed EmbeddingService's
// own ILogger rather than an ILogger<BatchEmbedder>: the log category stays "EmbeddingService"
// so existing App Insights queries and alert rules keep matching.
public sealed class BatchEmbedder
{
    // A cheap pre-filter in the wrong unit - see EmbedBatchAsync. Kept because it costs a length
    // check where the token count costs a tokenizer pass, and it catches the pathological case
    // before that pass runs.
    private const int TruncationLimit = 24_000;

    // text-embedding-3-large's per-input limit. The real one, in the unit the model counts in.
    private const int MaxInputTokens = 8_191;

    private readonly IEmbeddingClient _embeddingClient;
    private readonly int              _expectedDimensions;
    private readonly ILogger          _logger;

    public BatchEmbedder(IEmbeddingClient embeddingClient, int expectedDimensions, ILogger logger)
    {
        _embeddingClient    = embeddingClient;
        _expectedDimensions = expectedDimensions;
        _logger             = logger;
    }

    public async Task<BatchResult> EmbedBatchAsync(
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

            var (vectors, retries, throttledRetries, inputTokens) = await _embeddingClient.EmbedWithRetryAsync(texts, ct);
            if (retries > 0)
                Instrumentation.EmbeddingRetries.Add(retries);
            // Service-reported billed tokens (plan 1.6). Null = the response carried no usage;
            // nothing is recorded and the run total stays blank rather than under-counting.
            if (inputTokens is { } billed)
                Instrumentation.EmbeddingTokens.Add(billed);

            var results = new List<EmbedChunkResult>(batch.Count);
            for (int i = 0; i < batch.Count; i++)
            {
                var doc           = batch[i];
                doc.ContentVector = vectors[i];

                var dimError = doc.ContentVector?.Length != _expectedDimensions;
                if (dimError)
                {
                    _logger.LogError("Wrong vector dimensions {Dims} for {Id}", doc.ContentVector?.Length, doc.Id);
                    Instrumentation.VectorDimErrors.Add(1);
                }

                // Only checked on a vector of the right length: a wrong-length vector is
                // already counted once, and a second count for the same vector would make
                // one bad response read as two defects.
                var emptyVector = !dimError && doc.ContentVector is { } cv && VectorHealth.IsEmptyVector(cv);
                if (emptyVector)
                {
                    _logger.LogError("Empty vector (all-zero or non-finite) for {Id} — it will never match a query", doc.Id);
                    Instrumentation.EmptyVectors.Add(1);
                }

                results.Add(new EmbedChunkResult(doc, truncated[i], dimError, emptyVector));
            }

            return new BatchResult(results, retries, throttledRetries, inputTokens);
        }
        finally
        {
            semaphore.Release();
        }
    }
}

public record EmbedChunkResult(ChunkObject Document, bool Truncated, bool DimError, bool EmptyVector);
public record BatchResult(List<EmbedChunkResult> Results, int Retries, int ThrottledRetries, long? InputTokens);
