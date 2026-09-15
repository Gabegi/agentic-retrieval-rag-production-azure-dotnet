using System.ClientModel;
using System.Net.Http;
using Azure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgenticRagApp.Infrastructure.Clients.Embedding;

public class EmbeddingClient : IEmbeddingClient
{
    private const int MaxRetries = 5;

    private readonly IEmbeddingGenerator<string, Microsoft.Extensions.AI.Embedding<float>> _embeddingGenerator;
    private readonly ILogger<EmbeddingClient>                                              _logger;

    public EmbeddingClient(IEmbeddingGenerator<string, Microsoft.Extensions.AI.Embedding<float>> embeddingGenerator, ILogger<EmbeddingClient> logger)
    {
        _embeddingGenerator = embeddingGenerator;
        _logger              = logger;
    }

    public async Task<(float[][] Vectors, int Retries, int ThrottledRetries, long? InputTokens)> EmbedWithRetryAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var retries   = 0;
        var throttled = 0;

        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                var result = await _embeddingGenerator.GenerateAsync(texts, cancellationToken: ct);
                // Service-reported billed tokens, verbatim off the response usage; null when
                // the response carried none - see the interface comment.
                return (result.Select(e => e.Vector.ToArray()).ToArray(), retries, throttled, result.Usage?.InputTokenCount);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && IsRetryable(ex))
            {
                // Counted before the last-attempt rethrow below, so a call that exhausts its
                // retries and fails still reports the throttling that killed it - otherwise the
                // run where 429s did the most damage is the one that records none of them.
                if (IsThrottling(ex)) throttled++;

                if (attempt == MaxRetries - 1) throw;
                retries++;
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)); // 2s, 4s, 8s, 16s
                _logger.LogWarning(ex, "Embedding call failed, retry {Attempt}/{Max} in {Delay}s",
                    attempt + 1, MaxRetries, delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
        }

        throw new InvalidOperationException("Unreachable");
    }

    // Retries throttling (429), transient server-side failures (5xx), and network-level
    // faults — a dropped connection or timeout is no less recoverable than a 429.
    private static bool IsRetryable(Exception ex) => ex switch
    {
        ClientResultException  cre => cre.Status == 429 || cre.Status >= 500,
        RequestFailedException rfe => rfe.Status == 429 || rfe.Status >= 500,
        HttpRequestException        => true,
        TaskCanceledException       => true,   // request timeout, not caller cancellation (guarded above)
        _ => false
    };

    // The 429 subset of the above. Deliberately status-only: a timeout or a dropped connection
    // may well have been provoked by load, but attributing it to throttling without the status
    // saying so would inflate the one number a quota decision gets made on.
    private static bool IsThrottling(Exception ex) => ex switch
    {
        ClientResultException  cre => cre.Status == 429,
        RequestFailedException rfe => rfe.Status == 429,
        _ => false
    };
}
