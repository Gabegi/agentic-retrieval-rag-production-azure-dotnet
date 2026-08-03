using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Infrastructure.Clients.Blob;

namespace AgenticRagApp.Observability.Reports.Tests;

[TestClass]
public class SnapshotServiceReadLatestTests
{
    private static readonly IReadOnlyDictionary<string, string> NoMetadata = new Dictionary<string, string>();

    private static SnapshotService BuildService(Mock<IBlobStore> blobStore) =>
        new(blobStore.Object, new Mock<BlobContainerClient>().Object, NullLogger<SnapshotService>.Instance);

    [TestMethod]
    public async Task ReadLatestAsync_NoSnapshotsExist_ReturnsEmptyAndNullInstanceId()
    {
        var blobStore = new Mock<IBlobStore>();
        blobStore.Setup(s => s.ListBlobsAsync(It.IsAny<BlobContainerClient>(), "snapshots/pdf/", It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var service = BuildService(blobStore);

        var (chunks, instanceId) = await service.ReadLatestAsync("pdf");

        Assert.AreEqual(0, chunks.Count);
        Assert.IsNull(instanceId);
    }

    [TestMethod]
    public async Task ReadLatestAsync_MultipleGenerations_ReadsTheMostRecentOne()
    {
        var blobStore = new Mock<IBlobStore>();
        blobStore.Setup(s => s.ListBlobsAsync(It.IsAny<BlobContainerClient>(), "snapshots/pdf/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                ("snapshots/pdf/2024/01/01/instance-old/full-index.json", (DateTimeOffset?)DateTimeOffset.Parse("2024-01-01"), (long?)null, NoMetadata),
                ("snapshots/pdf/2024/06/01/instance-new/full-index.json", (DateTimeOffset?)DateTimeOffset.Parse("2024-06-01"), (long?)null, NoMetadata),
            ]);

        var expectedChunks = new List<SnapshotChunk>
        {
            new("id1", "doc1.pdf", "Title", null, "content", null, 0, 0, "hash1"),
        };
        blobStore.Setup(s => s.DownloadJsonAsync<List<SnapshotChunk>>(
                It.IsAny<BlobContainerClient>(), "snapshots/pdf/2024/06/01/instance-new/full-index.json", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedChunks);

        var service = BuildService(blobStore);

        var (chunks, instanceId) = await service.ReadLatestAsync("pdf");

        Assert.AreEqual("instance-new", instanceId);
        Assert.AreEqual(1, chunks.Count);
        Assert.AreEqual("id1", chunks[0].Id);
    }

    [TestMethod]
    public async Task ReadLatestAsync_LatestSnapshotUnreadable_ReturnsEmptyRatherThanThrowing()
    {
        var blobStore = new Mock<IBlobStore>();
        blobStore.Setup(s => s.ListBlobsAsync(It.IsAny<BlobContainerClient>(), "snapshots/pdf/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                ("snapshots/pdf/2024/06/01/instance-new/full-index.json", (DateTimeOffset?)DateTimeOffset.Parse("2024-06-01"), (long?)null, NoMetadata),
            ]);
        blobStore.Setup(s => s.DownloadJsonAsync<List<SnapshotChunk>>(
                It.IsAny<BlobContainerClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("corrupt"));

        var service = BuildService(blobStore);

        var (chunks, instanceId) = await service.ReadLatestAsync("pdf");

        Assert.AreEqual(0, chunks.Count);
        Assert.AreEqual("instance-new", instanceId);
    }
}
