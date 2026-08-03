using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Infrastructure.Clients.Blob;

namespace RagApp.UnitTests.Infrastructure.Blob;

[TestClass]
public class BlobStoreTests
{
    private const string BlobName = "state.json";

    private sealed record RunState(int CleanedRecords);

    private static (BlobStore Store, Mock<BlobContainerClient> Container, Mock<BlobClient> Blob) BuildStore()
    {
        var blob      = new Mock<BlobClient>();
        var container = new Mock<BlobContainerClient>();
        container.Setup(c => c.GetBlobClient(BlobName)).Returns(blob.Object);
        var store = new BlobStore(NullLogger<BlobStore>.Instance);
        return (store, container, blob);
    }

    [TestMethod]
    public async Task TryReadJsonWithETagAsync_BlobMissing_ReturnsNullValueAndETag()
    {
        var (store, container, blob) = BuildStore();
        blob.Setup(b => b.DownloadContentAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "not found"));

        var (value, etag) = await store.TryReadJsonWithETagAsync<RunState>(container.Object, BlobName);

        Assert.IsNull(value);
        Assert.IsNull(etag);
    }

    [TestMethod]
    public async Task TryReadJsonWithETagAsync_CorruptJson_TreatedAsNoBaseline()
    {
        var (store, container, blob) = BuildStore();
        var details = BlobsModelFactory.BlobDownloadDetails(eTag: new ETag("\"x\""));
        var result  = BlobsModelFactory.BlobDownloadResult(content: BinaryData.FromString("not-json-at-all"), details: details);
        blob.Setup(b => b.DownloadContentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(result, Mock.Of<Response>()));

        var (value, etag) = await store.TryReadJsonWithETagAsync<RunState>(container.Object, BlobName);

        Assert.IsNull(value);
        Assert.IsNull(etag);
    }

    [TestMethod]
    public async Task TryReadJsonWithETagAsync_ValidJson_ReturnsValueAndETag()
    {
        var (store, container, blob) = BuildStore();
        var etag    = new ETag("\"baseline\"");
        var details = BlobsModelFactory.BlobDownloadDetails(eTag: etag);
        var result  = BlobsModelFactory.BlobDownloadResult(content: BinaryData.FromString("{\"CleanedRecords\":100}"), details: details);
        blob.Setup(b => b.DownloadContentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(result, Mock.Of<Response>()));

        var (value, returnedEtag) = await store.TryReadJsonWithETagAsync<RunState>(container.Object, BlobName);

        Assert.AreEqual(100, value!.CleanedRecords);
        Assert.AreEqual(etag, returnedEtag);
    }

    [TestMethod]
    public async Task SaveJsonWithETagAsync_NoPreviousETag_UsesIfNoneMatchAll()
    {
        var (store, container, blob) = BuildStore();
        blob.Setup(b => b.UploadAsync(It.IsAny<BinaryData>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Response<BlobContentInfo>)null!);

        var saved = await store.SaveJsonWithETagAsync(container.Object, BlobName, new RunState(1), previousETag: null);

        Assert.IsTrue(saved);
        blob.Verify(b => b.UploadAsync(
            It.IsAny<BinaryData>(), It.Is<BlobUploadOptions>(o => o.Conditions!.IfNoneMatch == ETag.All), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task SaveJsonWithETagAsync_WithPreviousETag_UsesIfMatch()
    {
        var (store, container, blob) = BuildStore();
        var etag = new ETag("\"baseline\"");
        blob.Setup(b => b.UploadAsync(It.IsAny<BinaryData>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Response<BlobContentInfo>)null!);

        var saved = await store.SaveJsonWithETagAsync(container.Object, BlobName, new RunState(1), previousETag: etag);

        Assert.IsTrue(saved);
        blob.Verify(b => b.UploadAsync(
            It.IsAny<BinaryData>(), It.Is<BlobUploadOptions>(o => o.Conditions!.IfMatch == etag), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task SaveJsonWithETagAsync_ConcurrentWriteLostRace_ReturnsFalseInsteadOfThrowing()
    {
        var (store, container, blob) = BuildStore();
        blob.Setup(b => b.UploadAsync(It.IsAny<BinaryData>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(412, "precondition failed"));

        var saved = await store.SaveJsonWithETagAsync(container.Object, BlobName, new RunState(1), previousETag: new ETag("\"x\""));

        Assert.IsFalse(saved);
    }

    // Regression test for finding #17: Azure Blob Storage treats metadata key names as
    // case-insensitive, but the SDK hands back item.Metadata as an ordinal-comparer
    // dictionary. A manual upload setting "Zenya_Document_Id" must still be found by a
    // lookup for "zenya_document_id" (ZenyaMetadata.FromBlobMetadata), not silently read
    // as "not set".
    [TestMethod]
    public async Task ListBlobsAsync_MetadataLookupIsCaseInsensitive()
    {
        var container = new Mock<BlobContainerClient>();
        var items = new[]
        {
            BlobsModelFactory.BlobItem(
                name: "doc1.pdf",
                properties: BlobsModelFactory.BlobItemProperties(accessTierInferred: false),
                metadata: new Dictionary<string, string> { ["Zenya_Document_Id"] = "abc123" }),
        };
        var page = Page<BlobItem>.FromValues(items, continuationToken: null, response: Mock.Of<Response>());
        container.Setup(c => c.GetBlobsAsync(BlobTraits.Metadata, BlobStates.None, null, It.IsAny<CancellationToken>()))
            .Returns(AsyncPageable<BlobItem>.FromPages([page]));
        var store = new BlobStore(NullLogger<BlobStore>.Instance);

        var blobs = await store.ListBlobsAsync(container.Object);

        Assert.AreEqual(1, blobs.Count);
        Assert.IsTrue(blobs[0].Metadata.TryGetValue("zenya_document_id", out var value));
        Assert.AreEqual("abc123", value);
    }
}
