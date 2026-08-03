using Azure;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Infrastructure.Clients.Search;
using AgenticRagApp.Infrastructure.Configuration;

namespace RagApp.UnitTests.Infrastructure.Search;

[TestClass]
public class IndexServiceTests
{
    private static IndexerConfig Config() => new()
    {
        SearchEndpoint            = "https://search.example.com",
        OpenAiEndpoint            = "https://openai.example.com",
        OpenAiEmbeddingDeployment = "embed",
        StorageAccountUrl         = "https://storage.example.com",
        StorageContainer          = "container",
        SearchIndexName           = "my-index",
        KnowledgeSourceName       = "ks",
        KnowledgeBaseName         = "kb",
        OpenAiGptDeployment       = "gpt",
        OpenAiGptModelName        = "gpt-model",
    };

    private static (IndexService Service, Mock<SearchIndexClient> Client) BuildService()
    {
        var client  = new Mock<SearchIndexClient>();
        var service = new IndexService(Config(), client.Object, NullLogger<IndexService>.Instance);
        return (service, client);
    }

    [TestMethod]
    public async Task EnsureIndexAsync_IndexAlreadyExists_DoesNotCreateOrUpdate()
    {
        var (service, client) = BuildService();
        client.Setup(c => c.GetIndexAsync("my-index", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(new SearchIndex("my-index"), Mock.Of<Response>()));

        await service.EnsureIndexAsync();

        client.Verify(c => c.CreateOrUpdateIndexAsync(
            It.IsAny<SearchIndex>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task EnsureIndexAsync_IndexMissing_BuildsAndCreatesIndexForConfiguredName()
    {
        var (service, client) = BuildService();
        client.Setup(c => c.GetIndexAsync("my-index", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "not found"));
        client.Setup(c => c.CreateOrUpdateIndexAsync(
                It.IsAny<SearchIndex>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(new SearchIndex("my-index"), Mock.Of<Response>()));

        await service.EnsureIndexAsync();

        client.Verify(c => c.CreateOrUpdateIndexAsync(
            It.Is<SearchIndex>(i => i.Name == "my-index"), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task EnsureIndexAsync_UnexpectedFailureCheckingExistence_PropagatesWithoutCreating()
    {
        var (service, client) = BuildService();
        client.Setup(c => c.GetIndexAsync("my-index", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(500, "server error"));

        await Assert.ThrowsExactlyAsync<RequestFailedException>(() => service.EnsureIndexAsync());

        client.Verify(c => c.CreateOrUpdateIndexAsync(
            It.IsAny<SearchIndex>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task EnsureIndexAsync_IncludesTheContentVectorFieldSizedToConfiguredDimensions()
    {
        var (service, client) = BuildService();
        client.Setup(c => c.GetIndexAsync("my-index", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "not found"));
        SearchIndex? captured = null;
        client.Setup(c => c.CreateOrUpdateIndexAsync(
                It.IsAny<SearchIndex>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<SearchIndex, bool, bool, CancellationToken>((i, _, _, _) => captured = i)
            .ReturnsAsync(Response.FromValue(new SearchIndex("my-index"), Mock.Of<Response>()));

        await service.EnsureIndexAsync();

        Assert.IsNotNull(captured);
        var vectorField = captured!.Fields.Single(f => f.Name == "content_vector");
        Assert.AreEqual(3072, vectorField.VectorSearchDimensions);
    }

    [TestMethod]
    public async Task EnsureIndexAsync_IncludesNativePdfMetadataFields()
    {
        var (service, client) = BuildService();
        client.Setup(c => c.GetIndexAsync("my-index", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "not found"));
        SearchIndex? captured = null;
        client.Setup(c => c.CreateOrUpdateIndexAsync(
                It.IsAny<SearchIndex>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<SearchIndex, bool, bool, CancellationToken>((i, _, _, _) => captured = i)
            .ReturnsAsync(Response.FromValue(new SearchIndex("my-index"), Mock.Of<Response>()));

        await service.EnsureIndexAsync();

        Assert.IsNotNull(captured);

        var createdAt = captured!.Fields.Single(f => f.Name == "created_at");
        Assert.AreEqual(SearchFieldDataType.DateTimeOffset, createdAt.Type);
        Assert.IsTrue(createdAt.IsFilterable);
        Assert.IsTrue(createdAt.IsSortable);

        var modDate = captured.Fields.Single(f => f.Name == "mod_date");
        Assert.AreEqual(SearchFieldDataType.DateTimeOffset, modDate.Type);
        Assert.IsTrue(modDate.IsFilterable);
        Assert.IsTrue(modDate.IsSortable);

        var pageCount = captured.Fields.Single(f => f.Name == "page_count");
        Assert.AreEqual(SearchFieldDataType.Int32, pageCount.Type);
        Assert.IsTrue(pageCount.IsFilterable);
    }

    [TestMethod]
    public async Task DeleteIndexAsync_IndexExists_DeletesAndReturnsTrue_ViaRecreate()
    {
        var (service, client) = BuildService();
        client.Setup(c => c.DeleteIndexAsync("my-index", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<Response>());
        client.Setup(c => c.GetIndexAsync("my-index", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "not found"));
        client.Setup(c => c.CreateOrUpdateIndexAsync(
                It.IsAny<SearchIndex>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(new SearchIndex("my-index"), Mock.Of<Response>()));

        await service.RecreateIndexAsync();

        client.Verify(c => c.DeleteIndexAsync("my-index", It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task DeleteIndexAsync_IndexMissing_DoesNotThrow_ViaRecreate()
    {
        var (service, client) = BuildService();
        client.Setup(c => c.DeleteIndexAsync("my-index", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "not found"));
        client.Setup(c => c.GetIndexAsync("my-index", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "not found"));
        client.Setup(c => c.CreateOrUpdateIndexAsync(
                It.IsAny<SearchIndex>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(new SearchIndex("my-index"), Mock.Of<Response>()));

        await service.RecreateIndexAsync();
    }

    [TestMethod]
    public async Task RecreateIndexAsync_DeletesThenRecreatesTheConfiguredIndex()
    {
        var (service, client) = BuildService();
        client.Setup(c => c.DeleteIndexAsync("my-index", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<Response>());
        client.Setup(c => c.GetIndexAsync("my-index", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "not found"));
        client.Setup(c => c.CreateOrUpdateIndexAsync(
                It.IsAny<SearchIndex>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(new SearchIndex("my-index"), Mock.Of<Response>()));

        await service.RecreateIndexAsync();

        client.Verify(c => c.DeleteIndexAsync("my-index", It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(c => c.CreateOrUpdateIndexAsync(
            It.Is<SearchIndex>(i => i.Name == "my-index"), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
