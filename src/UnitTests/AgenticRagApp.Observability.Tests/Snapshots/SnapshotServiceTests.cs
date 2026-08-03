using AgenticRagApp.Common.Models;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Infrastructure.Clients.Blob;

namespace AgenticRagApp.Observability.Reports.Tests;

[TestClass]
public class SnapshotServiceTests
{
    private static readonly IReadOnlyDictionary<string, string> NoMetadata = new Dictionary<string, string>();
    private static readonly DateTimeOffset StartedAt = new(2024, 3, 15, 0, 0, 0, TimeSpan.Zero);

    private sealed record TestChunk(
        string Id, string DocumentId, string? Title, DateTimeOffset? LastModifiedDate,
        string Content, string? Heading, int PageNumber, int ChunkIndex, string ContentHash) : ISnapshotSource;

    private static SnapshotService BuildService(Mock<IBlobStore> blobStore) =>
        new(blobStore.Object, new Mock<BlobContainerClient>().Object, NullLogger<SnapshotService>.Instance);

    private static void SetupNoExisting(Mock<IBlobStore> blobStore, string source) =>
        blobStore.Setup(s => s.ListBlobsAsync(It.IsAny<BlobContainerClient>(), $"snapshots/{source}/", It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

    [TestMethod]
    public async Task UpdateAsync_NoPreviousSnapshot_WritesNewChunksAsIs()
    {
        var blobStore = new Mock<IBlobStore>();
        SetupNoExisting(blobStore, "pdf");
        var service = BuildService(blobStore);
        var newChunks = new List<TestChunk> { new("id1", "doc1", "Title", null, "content", null, 0, 0, "hash1") };

        var hashes = await service.UpdateAsync("pdf", newChunks, staleDocumentIds: [], instanceId: "run-1", StartedAt);

        Assert.AreEqual(1, hashes.Count);
        Assert.IsTrue(hashes.Contains("hash1"));
    }

    [TestMethod]
    public async Task UpdateAsync_MergesWithPreviousSnapshot_KeepingUntouchedDocuments()
    {
        var blobStore = new Mock<IBlobStore>();
        blobStore.Setup(s => s.ListBlobsAsync(It.IsAny<BlobContainerClient>(), "snapshots/pdf/", It.IsAny<CancellationToken>()))
            .ReturnsAsync([("snapshots/pdf/instance-old/full-index.json", (DateTimeOffset?)DateTimeOffset.Parse("2024-01-01"), (long?)null, NoMetadata)]);
        var previousChunks = new List<SnapshotChunk> { new("old-id", "doc-untouched", "Old", null, "old content", null, 0, 0, "old-hash") };
        blobStore.Setup(s => s.DownloadJsonAsync<List<SnapshotChunk>>(
                It.IsAny<BlobContainerClient>(), "snapshots/pdf/instance-old/full-index.json", It.IsAny<CancellationToken>()))
            .ReturnsAsync(previousChunks);
        var service = BuildService(blobStore);
        var newChunks = new List<TestChunk> { new("new-id", "doc-new", "New", null, "new content", null, 0, 0, "new-hash") };

        var hashes = await service.UpdateAsync("pdf", newChunks, staleDocumentIds: [], instanceId: "run-2", StartedAt);

        Assert.AreEqual(2, hashes.Count);
        Assert.IsTrue(hashes.Contains("old-hash"));
        Assert.IsTrue(hashes.Contains("new-hash"));
    }

    [TestMethod]
    public async Task UpdateAsync_StaleDocumentIds_DropsTheirPreviousEntries()
    {
        var blobStore = new Mock<IBlobStore>();
        blobStore.Setup(s => s.ListBlobsAsync(It.IsAny<BlobContainerClient>(), "snapshots/pdf/", It.IsAny<CancellationToken>()))
            .ReturnsAsync([("snapshots/pdf/instance-old/full-index.json", (DateTimeOffset?)DateTimeOffset.Parse("2024-01-01"), (long?)null, NoMetadata)]);
        var previousChunks = new List<SnapshotChunk>
        {
            new("stale-id", "doc-stale", "Stale", null, "stale content", null, 0, 0, "stale-hash"),
            new("keep-id", "doc-keep", "Keep", null, "keep content", null, 0, 0, "keep-hash"),
        };
        blobStore.Setup(s => s.DownloadJsonAsync<List<SnapshotChunk>>(
                It.IsAny<BlobContainerClient>(), "snapshots/pdf/instance-old/full-index.json", It.IsAny<CancellationToken>()))
            .ReturnsAsync(previousChunks);
        var service = BuildService(blobStore);

        var hashes = await service.UpdateAsync("pdf", new List<TestChunk>(), staleDocumentIds: ["doc-stale"], instanceId: "run-2", StartedAt);

        Assert.AreEqual(1, hashes.Count);
        Assert.IsTrue(hashes.Contains("keep-hash"));
        Assert.IsFalse(hashes.Contains("stale-hash"));
    }

    [TestMethod]
    public async Task UpdateAsync_StaleDocumentIds_MatchedCaseInsensitively()
    {
        var blobStore = new Mock<IBlobStore>();
        blobStore.Setup(s => s.ListBlobsAsync(It.IsAny<BlobContainerClient>(), "snapshots/pdf/", It.IsAny<CancellationToken>()))
            .ReturnsAsync([("snapshots/pdf/instance-old/full-index.json", (DateTimeOffset?)DateTimeOffset.Parse("2024-01-01"), (long?)null, NoMetadata)]);
        var previousChunks = new List<SnapshotChunk> { new("stale-id", "Doc-Stale", "Stale", null, "stale content", null, 0, 0, "stale-hash") };
        blobStore.Setup(s => s.DownloadJsonAsync<List<SnapshotChunk>>(
                It.IsAny<BlobContainerClient>(), "snapshots/pdf/instance-old/full-index.json", It.IsAny<CancellationToken>()))
            .ReturnsAsync(previousChunks);
        var service = BuildService(blobStore);

        var hashes = await service.UpdateAsync("pdf", new List<TestChunk>(), staleDocumentIds: ["doc-stale"], instanceId: "run-2", StartedAt);

        Assert.AreEqual(0, hashes.Count);
    }

    [TestMethod]
    public async Task UpdateAsync_WritesMergedSnapshotToInstanceScopedPath()
    {
        var blobStore = new Mock<IBlobStore>();
        SetupNoExisting(blobStore, "pdf");
        var service = BuildService(blobStore);

        await service.UpdateAsync("pdf", new List<TestChunk>(), staleDocumentIds: [], instanceId: "run-1", StartedAt);

        blobStore.Verify(s => s.UploadJsonAsync(
            It.IsAny<BlobContainerClient>(), "snapshots/pdf/2024/03/15/run-1/full-index.json", It.IsAny<List<SnapshotChunk>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task UpdateAsync_EnsuresContainerExistsBeforeWriting()
    {
        var blobStore = new Mock<IBlobStore>();
        SetupNoExisting(blobStore, "pdf");
        var service = BuildService(blobStore);

        await service.UpdateAsync("pdf", new List<TestChunk>(), staleDocumentIds: [], instanceId: "run-1", StartedAt);

        blobStore.Verify(s => s.EnsureContainerExistsAsync(It.IsAny<BlobContainerClient>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task UpdateAsync_FewerExistingSnapshotsThanRetentionLimit_PrunesNothing()
    {
        var blobStore = new Mock<IBlobStore>();
        blobStore.Setup(s => s.ListBlobsAsync(It.IsAny<BlobContainerClient>(), "snapshots/pdf/", It.IsAny<CancellationToken>()))
            .ReturnsAsync([("snapshots/pdf/instance-old/full-index.json", (DateTimeOffset?)DateTimeOffset.Parse("2024-01-01"), (long?)null, NoMetadata)]);
        blobStore.Setup(s => s.DownloadJsonAsync<List<SnapshotChunk>>(
                It.IsAny<BlobContainerClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var service = BuildService(blobStore);

        await service.UpdateAsync("pdf", new List<TestChunk>(), staleDocumentIds: [], instanceId: "run-2", StartedAt);

        blobStore.Verify(s => s.DeleteIfExistsAsync(It.IsAny<BlobContainerClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task UpdateAsync_MoreExistingSnapshotsThanRetentionLimit_PrunesOldestBeyondLimit()
    {
        // MaxRetainedSnapshots is 3; UpdateAsync just wrote a new one, so only the newest 2
        // of the pre-existing blobs survive - the rest (oldest first once sorted descending) get deleted.
        var blobStore = new Mock<IBlobStore>();
        blobStore.Setup(s => s.ListBlobsAsync(It.IsAny<BlobContainerClient>(), "snapshots/pdf/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                ("snapshots/pdf/instance-4/full-index.json", (DateTimeOffset?)DateTimeOffset.Parse("2024-04-01"), (long?)null, NoMetadata),
                ("snapshots/pdf/instance-3/full-index.json", (DateTimeOffset?)DateTimeOffset.Parse("2024-03-01"), (long?)null, NoMetadata),
                ("snapshots/pdf/instance-2/full-index.json", (DateTimeOffset?)DateTimeOffset.Parse("2024-02-01"), (long?)null, NoMetadata),
                ("snapshots/pdf/instance-1/full-index.json", (DateTimeOffset?)DateTimeOffset.Parse("2024-01-01"), (long?)null, NoMetadata),
            ]);
        blobStore.Setup(s => s.DownloadJsonAsync<List<SnapshotChunk>>(
                It.IsAny<BlobContainerClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var service = BuildService(blobStore);

        await service.UpdateAsync("pdf", new List<TestChunk>(), staleDocumentIds: [], instanceId: "run-new", StartedAt);

        blobStore.Verify(s => s.DeleteIfExistsAsync(It.IsAny<BlobContainerClient>(), "snapshots/pdf/instance-2/full-index.json", It.IsAny<CancellationToken>()), Times.Once);
        blobStore.Verify(s => s.DeleteIfExistsAsync(It.IsAny<BlobContainerClient>(), "snapshots/pdf/instance-1/full-index.json", It.IsAny<CancellationToken>()), Times.Once);
        blobStore.Verify(s => s.DeleteIfExistsAsync(It.IsAny<BlobContainerClient>(), "snapshots/pdf/instance-4/full-index.json", It.IsAny<CancellationToken>()), Times.Never);
        blobStore.Verify(s => s.DeleteIfExistsAsync(It.IsAny<BlobContainerClient>(), "snapshots/pdf/instance-3/full-index.json", It.IsAny<CancellationToken>()), Times.Never);
    }
}
