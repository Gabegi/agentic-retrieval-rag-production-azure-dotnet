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

        var (vectors, retries) = await client.EmbedWithRetryAsync(["a", "b"]);

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

        var (vectors, retries) = await client.EmbedWithRetryAsync(["a"]);

        Assert.AreEqual(2, attempts);
        Assert.AreEqual(1, retries);
        Assert.AreEqual(1, vectors.Length);
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
