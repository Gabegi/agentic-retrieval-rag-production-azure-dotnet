using Azure;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.KnowledgeBases.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Infrastructure.Clients.Search;
using AgenticRagApp.Infrastructure.Configuration;

namespace RagApp.UnitTests.Infrastructure.Search;

[TestClass]
public class KnowledgeServiceTests
{
    private static IndexerConfig Config() => new()
    {
        SearchEndpoint            = "https://search.example.com",
        OpenAiEndpoint            = "https://openai.example.com",
        OpenAiEmbeddingDeployment = "embed",
        StorageAccountUrl         = "https://storage.example.com",
        StorageContainer          = "container",
        SearchIndexName           = "my-index",
        KnowledgeSourceName       = "my-knowledge-source",
        KnowledgeBaseName         = "my-knowledge-base",
        OpenAiGptDeployment       = "gpt",
        OpenAiGptModelName        = "gpt-model",
    };

    private static (KnowledgeService Service, Mock<SearchIndexClient> Client) BuildService()
    {
        var client  = new Mock<SearchIndexClient>();
        var service = new KnowledgeService(Config(), client.Object, NullLogger<KnowledgeService>.Instance);
        return (service, client);
    }

    [TestMethod]
    public async Task EnsureKnowledgeSourceAsync_CreatesOrUpdatesWithConfiguredName()
    {
        var (service, client) = BuildService();

        await service.EnsureKnowledgeSourceAsync();

        client.Verify(c => c.CreateOrUpdateKnowledgeSourceAsync(
            It.Is<SearchIndexKnowledgeSource>(ks => ks.Name == "my-knowledge-source"), false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task EnsureKnowledgeSourceAsync_SourceDataFieldsIncludeNativePdfMetadata()
    {
        var (service, client) = BuildService();
        SearchIndexKnowledgeSource? captured = null;
        client.Setup(c => c.CreateOrUpdateKnowledgeSourceAsync(It.IsAny<SearchIndexKnowledgeSource>(), false, It.IsAny<CancellationToken>()))
            .Callback<KnowledgeSource, bool, CancellationToken>((ks, _, _) => captured = (SearchIndexKnowledgeSource)ks);

        await service.EnsureKnowledgeSourceAsync();

        Assert.IsNotNull(captured);
        var fieldNames = ((SearchIndexKnowledgeSourceParameters)captured!.SearchIndexParameters)
            .SourceDataFields.Select(f => f.Name).ToList();
        CollectionAssert.IsSubsetOf(new[] { "page_count", "created_at", "mod_date" }, fieldNames);
    }

    [TestMethod]
    public async Task EnsureKnowledgeBaseAsync_CreatesOrUpdatesWithConfiguredName()
    {
        var (service, client) = BuildService();

        await service.EnsureKnowledgeBaseAsync();

        client.Verify(c => c.CreateOrUpdateKnowledgeBaseAsync(
            It.Is<KnowledgeBase>(kb => kb.Name == "my-knowledge-base"), false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task DeleteKnowledgeBaseAsync_DeletesWithConfiguredName()
    {
        var (service, client) = BuildService();
        client.Setup(c => c.DeleteKnowledgeBaseAsync("my-knowledge-base", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<Response>());

        await service.DeleteKnowledgeBaseAsync();

        client.Verify(c => c.DeleteKnowledgeBaseAsync("my-knowledge-base", It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task DeleteKnowledgeBaseAsync_MissingBase_DoesNotThrow()
    {
        var (service, client) = BuildService();
        client.Setup(c => c.DeleteKnowledgeBaseAsync("my-knowledge-base", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "not found"));

        await service.DeleteKnowledgeBaseAsync();
    }

    [TestMethod]
    public async Task DeleteKnowledgeSourceAsync_DeletesWithConfiguredName()
    {
        var (service, client) = BuildService();
        client.Setup(c => c.DeleteKnowledgeSourceAsync("my-knowledge-source", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<Response>());

        await service.DeleteKnowledgeSourceAsync();

        client.Verify(c => c.DeleteKnowledgeSourceAsync("my-knowledge-source", It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task DeleteKnowledgeSourceAsync_MissingSource_DoesNotThrow()
    {
        var (service, client) = BuildService();
        client.Setup(c => c.DeleteKnowledgeSourceAsync("my-knowledge-source", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "not found"));

        await service.DeleteKnowledgeSourceAsync();
    }
}
