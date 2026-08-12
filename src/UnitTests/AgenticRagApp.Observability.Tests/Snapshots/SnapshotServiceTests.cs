using System.Text.Json;
using AgenticRagApp.Common.Models;
using Azure;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Infrastructure.Clients.Blob;

namespace AgenticRagApp.Observability.Reports.Tests;

[TestClass]
public class SnapshotServiceTests
{
    private static readonly DateTimeOffset StartedAt = new(2024, 3, 15, 0, 0, 0, TimeSpan.Zero);
    private const string PointerPath = "_latest-snapshot-pdf.json";

    private sealed record TestChunk(
        string Id, string DocumentId, string? Title, DateTimeOffset? LastModifiedDate,
        string Content, string? HeadingText, int PageStart, int ChildIndex, string ContentHash) : ISnapshotSource;

    private static SnapshotService BuildService(Mock<IBlobStore> blobStore) =>
        new(blobStore.Object, new Mock<BlobContainerClient>().Object, NullLogger<SnapshotService>.Instance);

    // No setup at all - Moq's default for an unconfigured generic call returns default(T),
    // i.e. (null, null) for the pointer read, mirroring "no snapshot exists yet" - same
    // pattern RunReportWriterTests uses for its own private-nested-type pointer.
    private static void SetupNoExistingPointer(Mock<IBlobStore> blobStore) { }

    private static void SetupExistingPointer(Mock<IBlobStore> blobStore, params (string Path, string InstanceId)[] entries) =>
        blobStore.Setup(s => s.TryReadJsonWithETagAsync<SnapshotService.SnapshotPointer>(
                It.IsAny<BlobContainerClient>(), PointerPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new SnapshotService.SnapshotPointer(
                entries.Select(e => new SnapshotService.SnapshotPointerEntry(e.Path, e.InstanceId)).ToList()), (ETag?)null));

    [TestMethod]
    public async Task UpdateAsync_NoPreviousSnapshot_WritesNewChunksAsIs()
    {
        var blobStore = new Mock<IBlobStore>();
        SetupNoExistingPointer(blobStore);
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
        SetupExistingPointer(blobStore, ("2024/01/01/ts-snapshot-pdf-instance-old.json", "instance-old"));
        var previousChunks = new List<SnapshotChunk> { new("old-id", "doc-untouched", "Old", null, "old content", null, 0, 0, "old-hash") };
        blobStore.Setup(s => s.DownloadJsonAsync<List<SnapshotChunk>>(
                It.IsAny<BlobContainerClient>(), "2024/01/01/ts-snapshot-pdf-instance-old.json", It.IsAny<CancellationToken>()))
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
        SetupExistingPointer(blobStore, ("2024/01/01/ts-snapshot-pdf-instance-old.json", "instance-old"));
        var previousChunks = new List<SnapshotChunk>
        {
            new("stale-id", "doc-stale", "Stale", null, "stale content", null, 0, 0, "stale-hash"),
            new("keep-id", "doc-keep", "Keep", null, "keep content", null, 0, 0, "keep-hash"),
        };
        blobStore.Setup(s => s.DownloadJsonAsync<List<SnapshotChunk>>(
                It.IsAny<BlobContainerClient>(), "2024/01/01/ts-snapshot-pdf-instance-old.json", It.IsAny<CancellationToken>()))
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
        SetupExistingPointer(blobStore, ("2024/01/01/ts-snapshot-pdf-instance-old.json", "instance-old"));
        var previousChunks = new List<SnapshotChunk> { new("stale-id", "Doc-Stale", "Stale", null, "stale content", null, 0, 0, "stale-hash") };
        blobStore.Setup(s => s.DownloadJsonAsync<List<SnapshotChunk>>(
                It.IsAny<BlobContainerClient>(), "2024/01/01/ts-snapshot-pdf-instance-old.json", It.IsAny<CancellationToken>()))
            .ReturnsAsync(previousChunks);
        var service = BuildService(blobStore);

        var hashes = await service.UpdateAsync("pdf", new List<TestChunk>(), staleDocumentIds: ["doc-stale"], instanceId: "run-2", StartedAt);

        Assert.AreEqual(0, hashes.Count);
    }

    [TestMethod]
    public async Task UpdateAsync_WritesMergedSnapshotToTheReportPathShapedName()
    {
        var blobStore = new Mock<IBlobStore>();
        SetupNoExistingPointer(blobStore);
        var service = BuildService(blobStore);

        await service.UpdateAsync("pdf", new List<TestChunk>(), staleDocumentIds: [], instanceId: "run-1", StartedAt);

        blobStore.Verify(s => s.UploadJsonAsync(
            It.IsAny<BlobContainerClient>(), "2024/03/15/20240315T000000000Z-snapshot-pdf-run-1.json", It.IsAny<List<SnapshotChunk>>(),
            It.IsAny<JsonSerializerOptions?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task UpdateAsync_EnsuresContainerExistsBeforeWriting()
    {
        var blobStore = new Mock<IBlobStore>();
        SetupNoExistingPointer(blobStore);
        var service = BuildService(blobStore);

        await service.UpdateAsync("pdf", new List<TestChunk>(), staleDocumentIds: [], instanceId: "run-1", StartedAt);

        blobStore.Verify(s => s.AssertContainerExistsAsync(It.IsAny<BlobContainerClient>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task UpdateAsync_UpdatesThePointerToTheNewSnapshot()
    {
        var blobStore = new Mock<IBlobStore>();
        SetupNoExistingPointer(blobStore);
        blobStore.Setup(s => s.SaveJsonWithETagAsync(
                It.IsAny<BlobContainerClient>(), PointerPath, It.IsAny<SnapshotService.SnapshotPointer>(), It.IsAny<ETag?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var service = BuildService(blobStore);

        await service.UpdateAsync("pdf", new List<TestChunk>(), staleDocumentIds: [], instanceId: "run-1", StartedAt);

        blobStore.Verify(s => s.SaveJsonWithETagAsync(
            It.IsAny<BlobContainerClient>(), PointerPath,
            It.Is<SnapshotService.SnapshotPointer>(p => p.Entries.Count == 1 && p.Entries[0].InstanceId == "run-1"),
            It.IsAny<ETag?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task UpdateAsync_FewerExistingSnapshotsThanRetentionLimit_PrunesNothing()
    {
        var blobStore = new Mock<IBlobStore>();
        SetupExistingPointer(blobStore, ("2024/01/01/ts-snapshot-pdf-instance-old.json", "instance-old"));
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
        // of the pre-existing (newest-first) pointer entries survive - the rest get deleted.
        var blobStore = new Mock<IBlobStore>();
        SetupExistingPointer(blobStore,
            ("2024/04/01/ts-snapshot-pdf-instance-4.json", "instance-4"),
            ("2024/03/01/ts-snapshot-pdf-instance-3.json", "instance-3"),
            ("2024/02/01/ts-snapshot-pdf-instance-2.json", "instance-2"),
            ("2024/01/01/ts-snapshot-pdf-instance-1.json", "instance-1"));
        blobStore.Setup(s => s.DownloadJsonAsync<List<SnapshotChunk>>(
                It.IsAny<BlobContainerClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var service = BuildService(blobStore);

        await service.UpdateAsync("pdf", new List<TestChunk>(), staleDocumentIds: [], instanceId: "run-new", StartedAt);

        blobStore.Verify(s => s.DeleteIfExistsAsync(It.IsAny<BlobContainerClient>(), "2024/02/01/ts-snapshot-pdf-instance-2.json", It.IsAny<CancellationToken>()), Times.Once);
        blobStore.Verify(s => s.DeleteIfExistsAsync(It.IsAny<BlobContainerClient>(), "2024/01/01/ts-snapshot-pdf-instance-1.json", It.IsAny<CancellationToken>()), Times.Once);
        blobStore.Verify(s => s.DeleteIfExistsAsync(It.IsAny<BlobContainerClient>(), "2024/04/01/ts-snapshot-pdf-instance-4.json", It.IsAny<CancellationToken>()), Times.Never);
        blobStore.Verify(s => s.DeleteIfExistsAsync(It.IsAny<BlobContainerClient>(), "2024/03/01/ts-snapshot-pdf-instance-3.json", It.IsAny<CancellationToken>()), Times.Never);
    }
}
