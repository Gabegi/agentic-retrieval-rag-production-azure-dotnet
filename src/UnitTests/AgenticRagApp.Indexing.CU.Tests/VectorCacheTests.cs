using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Moq;
using AgenticRagApp.Indexing.CU.Services;
using AgenticRagApp.Infrastructure.Clients.Blob;

namespace RagApp.UnitTests.Indexing;

[TestClass]
public class VectorCacheTests
{
    private static (VectorCache Cache, Mock<BlobContainerClient> Container, Mock<BlobClient> Blob) BuildCache()
    {
        var blob      = new Mock<BlobClient>();
        var container = new Mock<BlobContainerClient>();
        container.Setup(c => c.GetBlobClient(It.IsAny<string>())).Returns(blob.Object);
        var cache = new VectorCache(container.Object);
        return (cache, container, blob);
    }

    private static Response<BlobDownloadResult> DownloadResult(string content)
    {
        var result = BlobsModelFactory.BlobDownloadResult(content: BinaryData.FromString(content));
        return Response.FromValue(result, Mock.Of<Response>());
    }

    [TestMethod]
    public async Task TryGetAsync_BlobMissing_ReturnsNull()
    {
        var (cache, _, blob) = BuildCache();
        blob.Setup(b => b.DownloadContentAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "not found"));

        var result = await cache.TryGetAsync("hash1");

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task TryGetAsync_CorruptJson_ReturnsNullInsteadOfThrowing()
    {
        var (cache, _, blob) = BuildCache();
        blob.Setup(b => b.DownloadContentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(DownloadResult("not-json-at-all"));

        var result = await cache.TryGetAsync("hash1");

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task TryGetAsync_ValidJson_ReturnsVector()
    {
        var (cache, _, blob) = BuildCache();
        blob.Setup(b => b.DownloadContentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(DownloadResult("[1,2,3.5]"));

        var result = await cache.TryGetAsync("hash1");

        CollectionAssert.AreEqual(new float[] { 1, 2, 3.5f }, result);
    }

    [TestMethod]
    public async Task TryGetAsync_UsesContentHashScopedBlobPath()
    {
        var (cache, container, blob) = BuildCache();
        blob.Setup(b => b.DownloadContentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(DownloadResult("[1]"));

        await cache.TryGetAsync("abc123");

        container.Verify(c => c.GetBlobClient("vector-cache/abc123.json"), Times.Once);
    }

    // Until 2026-09-16 SetAsync called CreateIfNotExistsAsync before every upload - one extra
    // round trip per write (D197 action 1c). The container is Terraform-owned; the once-per-run
    // existence check is AssertContainerExistsAsync, tested below.
    [TestMethod]
    public async Task SetAsync_DoesNotCreateTheContainer()
    {
        var (cache, container, blob) = BuildCache();
        blob.Setup(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Response<BlobContentInfo>)null!);

        await cache.SetAsync("hash1", [1, 2, 3]);

        container.Verify(c => c.CreateIfNotExistsAsync(
            It.IsAny<PublicAccessType>(), It.IsAny<IDictionary<string, string>>(), It.IsAny<BlobContainerEncryptionScopeOptions>(), It.IsAny<CancellationToken>()), Times.Never);
        container.Verify(c => c.ExistsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task AssertContainerExistsAsync_ContainerMissing_ThrowsContainerNotDeclared()
    {
        var (cache, container, _) = BuildCache();
        container.Setup(c => c.Name).Returns("pipeline-artifacts");
        container.Setup(c => c.ExistsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(false, Mock.Of<Response>()));

        var ex = await Assert.ThrowsExactlyAsync<ContainerNotDeclaredException>(
            () => cache.AssertContainerExistsAsync());

        Assert.AreEqual("pipeline-artifacts", ex.ContainerName);
        container.Verify(c => c.CreateIfNotExistsAsync(
            It.IsAny<PublicAccessType>(), It.IsAny<IDictionary<string, string>>(), It.IsAny<BlobContainerEncryptionScopeOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task AssertContainerExistsAsync_ContainerPresent_Passes()
    {
        var (cache, container, _) = BuildCache();
        container.Setup(c => c.ExistsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(true, Mock.Of<Response>()));

        await cache.AssertContainerExistsAsync();

        container.Verify(c => c.ExistsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task SetAsync_UploadsWithOverwriteTrue_ToContentHashScopedPath()
    {
        var (cache, container, blob) = BuildCache();
        blob.Setup(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Response<BlobContentInfo>)null!);

        await cache.SetAsync("hash1", [1, 2, 3]);

        container.Verify(c => c.GetBlobClient("vector-cache/hash1.json"), Times.Once);
        blob.Verify(b => b.UploadAsync(It.IsAny<Stream>(), true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task EvictOrphanedAsync_DeletesBlobsNotInLiveHashes_KeepsTheRest()
    {
        var (cache, container, blob) = BuildCache();
        var deletedNames = new List<string>();
        container.Setup(c => c.GetBlobsAsync(BlobTraits.None, BlobStates.None, "vector-cache/", It.IsAny<CancellationToken>()))
            .Returns(BlobPage("vector-cache/live.json", "vector-cache/orphan.json"));
        container.Setup(c => c.GetBlobClient(It.IsAny<string>()))
            .Returns((string name) =>
            {
                deletedNames.Add(name);
                return blob.Object;
            });
        blob.Setup(b => b.DeleteIfExistsAsync(
                It.IsAny<DeleteSnapshotsOption>(), It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(true, Mock.Of<Response>()));

        var deletedCount = await cache.EvictOrphanedAsync(new HashSet<string> { "live" });

        Assert.AreEqual(1, deletedCount);
        CollectionAssert.Contains(deletedNames, "vector-cache/orphan.json");
        CollectionAssert.DoesNotContain(deletedNames, "vector-cache/live.json");
    }

    [TestMethod]
    public async Task EvictOrphanedAsync_NoOrphans_ReturnsZeroWithoutDeleting()
    {
        var (cache, container, blob) = BuildCache();
        container.Setup(c => c.GetBlobsAsync(BlobTraits.None, BlobStates.None, "vector-cache/", It.IsAny<CancellationToken>()))
            .Returns(BlobPage("vector-cache/live.json"));

        var deletedCount = await cache.EvictOrphanedAsync(new HashSet<string> { "live" });

        Assert.AreEqual(0, deletedCount);
        blob.Verify(b => b.DeleteIfExistsAsync(
            It.IsAny<DeleteSnapshotsOption>(), It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static AsyncPageable<BlobItem> BlobPage(params string[] names)
    {
        var items    = names.Select(n => BlobsModelFactory.BlobItem(name: n)).ToList();
        var page     = Page<BlobItem>.FromValues(items, continuationToken: null, response: Mock.Of<Response>());
        return AsyncPageable<BlobItem>.FromPages([page]);
    }
}
