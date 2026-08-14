using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Moq;
using AgenticRagApp.Infrastructure.Clients.DocumentIdentity;

namespace RagApp.UnitTests.Infrastructure;

// Covers the eviction path only - the rest of the store (GetAll/Set) is exercised through
// DocumentIdentityResolverTests against a mocked store.
[TestClass]
public class DocumentIdentityStoreTests
{
    private const string ModelId = "text-embedding-3-large@3072";

    private static DocumentIdentityRecord Record(string sourceId) =>
        new(sourceId, sourceId, null, [1f, 0f, 0f], "family", "hash", ModelId);

    private static Response<BlobDownloadResult> Download(DocumentIdentityRecord record) =>
        Response.FromValue(
            BlobsModelFactory.BlobDownloadResult(
                content: BinaryData.FromBytes(JsonSerializer.SerializeToUtf8Bytes(record))),
            Mock.Of<Response>());

    private static AsyncPageable<BlobItem> BlobPage(params string[] names)
    {
        var items = names.Select(n => BlobsModelFactory.BlobItem(name: n)).ToList();
        var page  = Page<BlobItem>.FromValues(items, continuationToken: null, response: Mock.Of<Response>());
        return AsyncPageable<BlobItem>.FromPages([page]);
    }

    [TestMethod]
    public async Task EvictOrphanedAsync_DeletesRecordsForDocumentsNoLongerInTheCorpus()
    {
        // A deleted document's identity record used to live forever, and a ghost record does
        // real damage: single-linkage means one sitting between two live documents merges their
        // families, and it can even be the family's id.
        var container = new Mock<BlobContainerClient>();
        var liveBlob  = new Mock<BlobClient>();
        var ghostBlob = new Mock<BlobClient>();

        container
            .Setup(c => c.GetBlobsAsync(BlobTraits.None, BlobStates.None, "document-identity/", It.IsAny<CancellationToken>()))
            .Returns(BlobPage("document-identity/live.json", "document-identity/ghost.json"));

        container.Setup(c => c.GetBlobClient("document-identity/live.json")).Returns(liveBlob.Object);
        container.Setup(c => c.GetBlobClient("document-identity/ghost.json")).Returns(ghostBlob.Object);

        liveBlob.Setup(b => b.DownloadContentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Download(Record("live.pdf")));
        ghostBlob.Setup(b => b.DownloadContentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Download(Record("deleted.pdf")));
        ghostBlob.Setup(b => b.DeleteIfExistsAsync(
                It.IsAny<DeleteSnapshotsOption>(), It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(true, Mock.Of<Response>()));

        var store = new DocumentIdentityStore(container.Object);

        var deleted = await store.EvictOrphanedAsync(new HashSet<string> { "live.pdf" });

        Assert.AreEqual(1, deleted);
        ghostBlob.Verify(b => b.DeleteIfExistsAsync(
            It.IsAny<DeleteSnapshotsOption>(), It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()), Times.Once);
        liveBlob.Verify(b => b.DeleteIfExistsAsync(
            It.IsAny<DeleteSnapshotsOption>(), It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task EvictOrphanedAsync_EmptyLiveSet_DeletesNothing()
    {
        // An empty live set means the snapshot is empty - a first run, or one that has not been
        // built yet - not that the whole corpus was deleted. Acting on it would wipe the store.
        var container = new Mock<BlobContainerClient>();
        var store     = new DocumentIdentityStore(container.Object);

        var deleted = await store.EvictOrphanedAsync(new HashSet<string>());

        Assert.AreEqual(0, deleted);
        container.Verify(c => c.GetBlobsAsync(
            It.IsAny<BlobTraits>(), It.IsAny<BlobStates>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task EvictOrphanedAsync_UnreadableRecord_IsLeftAloneRatherThanDeleted()
    {
        // Its SourceId is unknown, so it cannot be matched against the live set. GetAllAsync
        // already skips it, so it is inert - and "we couldn't parse it" is the wrong reason to
        // delete the only durable copy of a document's identity.
        var container = new Mock<BlobContainerClient>();
        var blob      = new Mock<BlobClient>();

        container
            .Setup(c => c.GetBlobsAsync(BlobTraits.None, BlobStates.None, "document-identity/", It.IsAny<CancellationToken>()))
            .Returns(BlobPage("document-identity/corrupt.json"));
        container.Setup(c => c.GetBlobClient(It.IsAny<string>())).Returns(blob.Object);
        blob.Setup(b => b.DownloadContentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(
                BlobsModelFactory.BlobDownloadResult(content: BinaryData.FromString("not-json-at-all")),
                Mock.Of<Response>()));

        var store = new DocumentIdentityStore(container.Object);

        var deleted = await store.EvictOrphanedAsync(new HashSet<string> { "live.pdf" });

        Assert.AreEqual(0, deleted);
        blob.Verify(b => b.DeleteIfExistsAsync(
            It.IsAny<DeleteSnapshotsOption>(), It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
