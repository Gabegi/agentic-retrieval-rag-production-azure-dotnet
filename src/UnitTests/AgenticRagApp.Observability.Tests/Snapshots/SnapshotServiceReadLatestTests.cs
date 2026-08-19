using Azure;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Infrastructure.Clients.Blob;

namespace AgenticRagApp.Observability.Reports.Tests;

[TestClass]
public class SnapshotServiceReadLatestTests
{
    private const string PointerPath = "_latest-snapshot-pdf.json";

    private static SnapshotService BuildService(Mock<IBlobStore> blobStore) =>
        new(blobStore.Object, new Mock<BlobContainerClient>().Object, NullLogger<SnapshotService>.Instance);

    [TestMethod]
    public async Task ReadLatestAsync_NoSnapshotsExist_ReturnsEmptyAndNullInstanceId()
    {
        // No pointer setup - Moq's default for an unconfigured generic call returns
        // default(T), i.e. (null, null), mirroring "no snapshot pointer exists yet".
        var blobStore = new Mock<IBlobStore>();
        var service = BuildService(blobStore);

        var (chunks, instanceId) = await service.ReadLatestAsync("pdf");

        Assert.AreEqual(0, chunks.Count);
        Assert.IsNull(instanceId);
    }

    [TestMethod]
    public async Task ReadLatestAsync_PointerHasEntries_ReadsTheFirstOne()
    {
        var blobStore = new Mock<IBlobStore>();
        blobStore.Setup(s => s.TryReadJsonWithETagAsync<SnapshotService.SnapshotPointer>(
                It.IsAny<BlobContainerClient>(), PointerPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new SnapshotService.SnapshotPointer(
            [
                new SnapshotService.SnapshotPointerEntry("2024/06/01/ts-snapshot-pdf-instance-new.json", "instance-new"),
                new SnapshotService.SnapshotPointerEntry("2024/01/01/ts-snapshot-pdf-instance-old.json", "instance-old"),
            ]), (ETag?)null));

        var expectedChunks = new List<SnapshotChunk>
        {
            TestChunk.Snapshot("id1", "doc1.pdf", "Title", "content", "hash1"),
        };
        blobStore.Setup(s => s.DownloadJsonAsync<List<SnapshotChunk>>(
                It.IsAny<BlobContainerClient>(), "2024/06/01/ts-snapshot-pdf-instance-new.json", It.IsAny<CancellationToken>()))
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
        blobStore.Setup(s => s.TryReadJsonWithETagAsync<SnapshotService.SnapshotPointer>(
                It.IsAny<BlobContainerClient>(), PointerPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new SnapshotService.SnapshotPointer(
            [
                new SnapshotService.SnapshotPointerEntry("2024/06/01/ts-snapshot-pdf-instance-new.json", "instance-new"),
            ]), (ETag?)null));
        blobStore.Setup(s => s.DownloadJsonAsync<List<SnapshotChunk>>(
                It.IsAny<BlobContainerClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("corrupt"));

        var service = BuildService(blobStore);

        var (chunks, instanceId) = await service.ReadLatestAsync("pdf");

        Assert.AreEqual(0, chunks.Count);
        Assert.AreEqual("instance-new", instanceId);
    }
}
