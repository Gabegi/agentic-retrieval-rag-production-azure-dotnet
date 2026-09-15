using Azure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Infrastructure.Clients.Embedding;

namespace RagApp.UnitTests.Infrastructure.Embedding;

[TestClass]
public class EmbeddingClientTests
{
    private static GeneratedEmbeddings<Microsoft.Extensions.AI.Embedding<float>> Embeddings(int count, int dims = 4) =>
        new(Enumerable.Range(0, count).Select(_ => new Microsoft.Extensions.AI.Embedding<float>(new float[dims])));

    private static (EmbeddingClient Client, Mock<IEmbeddingGenerator<string, Microsoft.Extensions.AI.Embedding<float>>> Generator) BuildClient()
    {
        var generator = new Mock<IEmbeddingGenerator<string, Microsoft.Extensions.AI.Embedding<float>>>();
        var client    = new EmbeddingClient(generator.Object, NullLogger<EmbeddingClient>.Instance);
        return (client, generator);
    }

    [TestMethod]
    public async Task EmbedWithRetryAsync_Success_ReturnsVectorsWithZeroRetries()
    {
        var (client, generator) = BuildClient();
        generator
            .Setup(g => g.GenerateAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<EmbeddingGenerationOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> values, EmbeddingGenerationOptions? _, CancellationToken _) => Embeddings(values.Count()));

        var (vectors, retries, throttledRetries, inputTokens) = await client.EmbedWithRetryAsync(["a", "b"]);

        Assert.AreEqual(2, vectors.Length);
        Assert.AreEqual(0, retries);
    }

    [TestMethod]
    public async Task EmbedWithRetryAsync_TransientFailureThenSuccess_RetriesAndSucceeds()
    {
        var attempts = 0;
        var (client, generator) = BuildClient();
        generator
            .Setup(g => g.GenerateAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<EmbeddingGenerationOptions>(), It.IsAny<CancellationToken>()))
            .Returns(async (IEnumerable<string> values, EmbeddingGenerationOptions? _, CancellationToken ct) =>
            {
                attempts++;
                if (attempts == 1)
                    throw new RequestFailedException(429, "throttled");
                return Embeddings(values.Count());
            });

        var (vectors, retries, throttled, _) = await client.EmbedWithRetryAsync(["a"]);

        Assert.AreEqual(2, attempts);
        Assert.AreEqual(1, retries);
        Assert.AreEqual(1, vectors.Length);
        // The retry was a 429, so it counts on both totals.
        Assert.AreEqual(1, throttled);
    }

    [TestMethod]
    public async Task EmbedWithRetryAsync_CountsOnly429sAsThrottling_NotServerErrors()
    {
        // The whole reason ThrottledRetries exists: a 5xx and a 429 both retry, but they point at
        // different remedies - raise TPM / lower parallelism versus wait for the service. Folding
        // them together is what made "were we rate-limited" unanswerable from a run report.
        var attempts = 0;
        var (client, generator) = BuildClient();
        generator
            .Setup(g => g.GenerateAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<EmbeddingGenerationOptions>(), It.IsAny<CancellationToken>()))
            .Returns(async (IEnumerable<string> values, EmbeddingGenerationOptions? _, CancellationToken ct) =>
            {
                attempts++;
                if (attempts == 1) throw new RequestFailedException(503, "service unavailable");
                if (attempts == 2) throw new RequestFailedException(429, "throttled");
                return Embeddings(values.Count());
            });

        var (_, retries, throttled, _) = await client.EmbedWithRetryAsync(["a"]);

        Assert.AreEqual(2, retries);
        Assert.AreEqual(1, throttled);
    }

    [TestMethod]
    public async Task EmbedWithRetryAsync_NetworkFaults_AreRetriedButNotCountedAsThrottling()
    {
        // A timeout may well have been provoked by load, but attributing it to throttling without
        // a 429 saying so would inflate the one number a quota decision gets made on.
        var attempts = 0;
        var (client, generator) = BuildClient();
        generator
            .Setup(g => g.GenerateAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<EmbeddingGenerationOptions>(), It.IsAny<CancellationToken>()))
            .Returns(async (IEnumerable<string> values, EmbeddingGenerationOptions? _, CancellationToken ct) =>
            {
                attempts++;
                if (attempts == 1) throw new TaskCanceledException("request timeout");
                return Embeddings(values.Count());
            });

        var (_, retries, throttled, _) = await client.EmbedWithRetryAsync(["a"]);

        Assert.AreEqual(1, retries);
        Assert.AreEqual(0, throttled);
    }

    [TestMethod]
    public async Task EmbedWithRetryAsync_NonRetryableFailure_PropagatesImmediately()
    {
        var attempts = 0;
        var (client, generator) = BuildClient();
        generator
            .Setup(g => g.GenerateAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<EmbeddingGenerationOptions>(), It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<string>, EmbeddingGenerationOptions?, CancellationToken>((_, _, _) =>
            {
                attempts++;
                throw new InvalidOperationException("not retryable");
            });

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => client.EmbedWithRetryAsync(["a"]));
        Assert.AreEqual(1, attempts);
    }
}
