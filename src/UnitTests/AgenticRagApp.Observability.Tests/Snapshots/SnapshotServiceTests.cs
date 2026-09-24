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

        var hashes = await service.UpdateAsync("pdf", newChunks, staleDocumentIds: [], processedDocumentIds: [], instanceId: "run-1", startedAt: StartedAt);

        Assert.AreEqual(1, hashes.ContentHashes.Count);
        Assert.IsTrue(hashes.ContentHashes.Contains("hash1"));
        Assert.IsTrue(hashes.DocumentIds.Contains("doc1"));
    }

    [TestMethod]
    public async Task UpdateAsync_MergesWithPreviousSnapshot_KeepingUntouchedDocuments()
    {
        var blobStore = new Mock<IBlobStore>();
        SetupExistingPointer(blobStore, ("2024/01/01/ts-snapshot-pdf-instance-old.json", "instance-old"));
        var previousChunks = new List<SnapshotChunk> { TestChunk.Snapshot("old-id", "doc-untouched", "Old", "old content", "old-hash") };
        blobStore.Setup(s => s.DownloadJsonAsync<List<SnapshotChunk>>(
                It.IsAny<BlobContainerClient>(), "2024/01/01/ts-snapshot-pdf-instance-old.json", It.IsAny<CancellationToken>()))
            .ReturnsAsync(previousChunks);
        var service = BuildService(blobStore);
        var newChunks = new List<TestChunk> { new("new-id", "doc-new", "New", null, "new content", null, 0, 0, "new-hash") };

        var hashes = await service.UpdateAsync("pdf", newChunks, staleDocumentIds: [], processedDocumentIds: [], instanceId: "run-2", startedAt: StartedAt);

        Assert.AreEqual(2, hashes.ContentHashes.Count);
        Assert.IsTrue(hashes.ContentHashes.Contains("old-hash"));
        Assert.IsTrue(hashes.ContentHashes.Contains("new-hash"));

        // Both grains come off the same merged list, so the document ids agree with the hashes
        // about what survived - the identity store's eviction depends on that.
        Assert.AreEqual(2, hashes.DocumentIds.Count);
        Assert.IsTrue(hashes.DocumentIds.Contains("doc-untouched"));
        Assert.IsTrue(hashes.DocumentIds.Contains("doc-new"));
    }

    [TestMethod]
    public async Task UpdateAsync_StaleDocumentIds_DropsTheirPreviousEntries()
    {
        var blobStore = new Mock<IBlobStore>();
        SetupExistingPointer(blobStore, ("2024/01/01/ts-snapshot-pdf-instance-old.json", "instance-old"));
        var previousChunks = new List<SnapshotChunk>
        {
            TestChunk.Snapshot("stale-id", "doc-stale", "Stale", "stale content", "stale-hash"),
            TestChunk.Snapshot("keep-id", "doc-keep", "Keep", "keep content", "keep-hash"),
        };
        blobStore.Setup(s => s.DownloadJsonAsync<List<SnapshotChunk>>(
                It.IsAny<BlobContainerClient>(), "2024/01/01/ts-snapshot-pdf-instance-old.json", It.IsAny<CancellationToken>()))
            .ReturnsAsync(previousChunks);
        var service = BuildService(blobStore);

        var hashes = await service.UpdateAsync("pdf", new List<TestChunk>(), staleDocumentIds: ["doc-stale"], processedDocumentIds: [], instanceId: "run-2", startedAt: StartedAt);

        Assert.AreEqual(1, hashes.ContentHashes.Count);
        Assert.IsTrue(hashes.ContentHashes.Contains("keep-hash"));
        Assert.IsFalse(hashes.ContentHashes.Contains("stale-hash"));

        // The removed document must not appear live, or the identity store would keep its
        // record forever.
        Assert.IsFalse(hashes.DocumentIds.Contains("doc-stale"));
    }

    [TestMethod]
    public async Task UpdateAsync_StaleDocumentIds_MatchedCaseInsensitively()
    {
        var blobStore = new Mock<IBlobStore>();
        SetupExistingPointer(blobStore, ("2024/01/01/ts-snapshot-pdf-instance-old.json", "instance-old"));
        var previousChunks = new List<SnapshotChunk> { TestChunk.Snapshot("stale-id", "Doc-Stale", "Stale", "stale content", "stale-hash") };
        blobStore.Setup(s => s.DownloadJsonAsync<List<SnapshotChunk>>(
                It.IsAny<BlobContainerClient>(), "2024/01/01/ts-snapshot-pdf-instance-old.json", It.IsAny<CancellationToken>()))
            .ReturnsAsync(previousChunks);
        var service = BuildService(blobStore);

        var hashes = await service.UpdateAsync("pdf", new List<TestChunk>(), staleDocumentIds: ["doc-stale"], processedDocumentIds: [], instanceId: "run-2", startedAt: StartedAt);

        Assert.AreEqual(0, hashes.ContentHashes.Count);
        Assert.AreEqual(0, hashes.DocumentIds.Count);
    }

    [TestMethod]
    public async Task UpdateAsync_WritesMergedSnapshotToTheReportPathShapedName()
    {
        var blobStore = new Mock<IBlobStore>();
        SetupNoExistingPointer(blobStore);
        var service = BuildService(blobStore);

        await service.UpdateAsync("pdf", new List<TestChunk>(), staleDocumentIds: [], processedDocumentIds: [], instanceId: "run-1", startedAt: StartedAt);

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

        await service.UpdateAsync("pdf", new List<TestChunk>(), staleDocumentIds: [], processedDocumentIds: [], instanceId: "run-1", startedAt: StartedAt);

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

        await service.UpdateAsync("pdf", new List<TestChunk>(), staleDocumentIds: [], processedDocumentIds: [], instanceId: "run-1", startedAt: StartedAt);

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

        await service.UpdateAsync("pdf", new List<TestChunk>(), staleDocumentIds: [], processedDocumentIds: [], instanceId: "run-2", startedAt: StartedAt);

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

        await service.UpdateAsync("pdf", new List<TestChunk>(), staleDocumentIds: [], processedDocumentIds: [], instanceId: "run-new", startedAt: StartedAt);

        blobStore.Verify(s => s.DeleteIfExistsAsync(It.IsAny<BlobContainerClient>(), "2024/02/01/ts-snapshot-pdf-instance-2.json", It.IsAny<CancellationToken>()), Times.Once);
        blobStore.Verify(s => s.DeleteIfExistsAsync(It.IsAny<BlobContainerClient>(), "2024/01/01/ts-snapshot-pdf-instance-1.json", It.IsAny<CancellationToken>()), Times.Once);
        blobStore.Verify(s => s.DeleteIfExistsAsync(It.IsAny<BlobContainerClient>(), "2024/04/01/ts-snapshot-pdf-instance-4.json", It.IsAny<CancellationToken>()), Times.Never);
        blobStore.Verify(s => s.DeleteIfExistsAsync(It.IsAny<BlobContainerClient>(), "2024/03/01/ts-snapshot-pdf-instance-3.json", It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── The drop set (D200 R1, 2026-09-17) ───────────────────────────────────

    private static Mock<IBlobStore> WithPrevious(params SnapshotChunk[] previous)
    {
        var blobStore = new Mock<IBlobStore>();
        SetupExistingPointer(blobStore, ("2024/01/01/ts-snapshot-pdf-instance-old.json", "instance-old"));
        blobStore.Setup(s => s.DownloadJsonAsync<List<SnapshotChunk>>(
                It.IsAny<BlobContainerClient>(), "2024/01/01/ts-snapshot-pdf-instance-old.json", It.IsAny<CancellationToken>()))
            .ReturnsAsync(previous.ToList());
        return blobStore;
    }

    // THE regression test for D200 F1.
    //
    // On the daily run the index is recreated first, so the diff sees an empty index, every
    // document reads as NEW rather than updated, and staleDocumentIds is empty. Before the fix
    // that meant nothing was dropped and each run's whole corpus was appended to the last: 65,728
    // rows against ~3,700 live chunks by 09-15, 93.4% superseded, +8 MB every run. The blast
    // radius was not the size - EvictOrphanedAsync received every hash ever recorded, so no cache
    // entry could ever look orphaned and eviction was silently dead.
    //
    // If this test ever fails by growing, that is the defect coming back.
    [TestMethod]
    public async Task UpdateAsync_ReprocessedDocumentWithNoStaleIds_DropsItsPreviousRows()
    {
        var blobStore = WithPrevious(
            TestChunk.Snapshot("doc1-c1", "doc1.pdf", "Doc 1", "old content", "old-hash-1"),
            TestChunk.Snapshot("doc1-c2", "doc1.pdf", "Doc 1", "old content", "old-hash-2"),
            TestChunk.Snapshot("doc2-c1", "doc2.pdf", "Doc 2", "untouched", "keep-hash"));
        var service = BuildService(blobStore);

        // doc1 was re-extracted; doc2 was not touched. staleDocumentIds is empty, as it is on
        // every recreate run.
        var live = await service.UpdateAsync(
            "pdf",
            new List<TestChunk> { new("doc1-c1", "doc1.pdf", "Doc 1", null, "new content", null, 0, 0, "new-hash-1") },
            staleDocumentIds: [],
            processedDocumentIds: ["doc1.pdf"],
            instanceId: "run-2", startedAt: StartedAt);

        // doc1's two superseded rows are gone, its one fresh row is in, doc2 is untouched.
        Assert.AreEqual(2, live.ContentHashes.Count, "one fresh doc1 row plus doc2's untouched row");
        Assert.IsTrue(live.ContentHashes.Contains("new-hash-1"));
        Assert.IsTrue(live.ContentHashes.Contains("keep-hash"), "a document this run did not touch keeps its rows");
        Assert.IsFalse(live.ContentHashes.Contains("old-hash-1"), "superseded rows must not survive — this is D200 F1");
        Assert.IsFalse(live.ContentHashes.Contains("old-hash-2"));
    }

    // A document that was processed but produced NO chunks KEEPS its rows.
    //
    // The two failure modes are not symmetric, and this test is the asymmetry. A document that
    // fails between extraction and chunking is still in processedDocumentIds; dropping its rows
    // with nothing to re-add deletes it from the snapshot, and the snapshot is what a restore
    // rebuilds the index from - so one transient failure would cost a document at the next
    // restore. Keeping superseded rows for a genuinely chunk-less document is only the leak this
    // change exists to reduce, and any later run fixes it.
    //
    // Retain the leak, never the data loss.
    [TestMethod]
    public async Task UpdateAsync_ProcessedDocumentThatProducedNoChunks_KeepsItsRows()
    {
        var blobStore = WithPrevious(
            TestChunk.Snapshot("doc1-c1", "doc1.pdf", "Doc 1", "old content", "old-hash-1"));
        var service = BuildService(blobStore);

        var live = await service.UpdateAsync(
            "pdf", new List<TestChunk>(),
            staleDocumentIds: [], processedDocumentIds: ["doc1.pdf"],
            instanceId: "run-2", startedAt: StartedAt);

        Assert.AreEqual(1, live.ContentHashes.Count,
            "a processed document that produced no chunks must not be deleted from the snapshot — restore reads this");
        Assert.IsTrue(live.ContentHashes.Contains("old-hash-1"));
    }

    // But an EXPLICITLY stale document with no chunks still goes: the diff said it was updated or
    // removed, which is a statement about the document, not an accident of the chunker.
    [TestMethod]
    public async Task UpdateAsync_StaleDocumentThatProducedNoChunks_StillLosesItsRows()
    {
        var blobStore = WithPrevious(
            TestChunk.Snapshot("doc1-c1", "doc1.pdf", "Doc 1", "old content", "old-hash-1"));
        var service = BuildService(blobStore);

        var live = await service.UpdateAsync(
            "pdf", new List<TestChunk>(),
            staleDocumentIds: ["doc1.pdf"], processedDocumentIds: ["doc1.pdf"],
            instanceId: "run-2", startedAt: StartedAt);

        Assert.AreEqual(0, live.ContentHashes.Count);
    }

    // Union, not replacement: a REMOVED document is stale but never processed - it no longer
    // exists to extract - and its rows still have to go.
    [TestMethod]
    public async Task UpdateAsync_StaleButNotProcessed_StillDropped()
    {
        var blobStore = WithPrevious(
            TestChunk.Snapshot("gone-c1", "removed.pdf", "Removed", "old content", "gone-hash"));
        var service = BuildService(blobStore);

        var live = await service.UpdateAsync(
            "pdf", new List<TestChunk>(),
            staleDocumentIds: ["removed.pdf"], processedDocumentIds: [],
            instanceId: "run-2", startedAt: StartedAt);

        Assert.AreEqual(0, live.ContentHashes.Count);
    }

    // Casing must not decide whether rows are superseded, and there are now THREE spellings that
    // have to agree: the previous row's DocumentId, the processed list's SourceId, and the new
    // chunk's DocumentId. All three differ here on purpose.
    [TestMethod]
    public async Task UpdateAsync_ProcessedDocumentIds_MatchedCaseInsensitively()
    {
        var blobStore = WithPrevious(
            TestChunk.Snapshot("doc1-c1", "Doc1.PDF", "Doc 1", "old content", "old-hash"));
        var service = BuildService(blobStore);

        var live = await service.UpdateAsync(
            "pdf",
            new List<TestChunk> { new("doc1-c1", "DOC1.pdf", "Doc 1", null, "new content", null, 0, 0, "new-hash") },
            staleDocumentIds: [], processedDocumentIds: ["doc1.pdf"],
            instanceId: "run-2", startedAt: StartedAt);

        Assert.AreEqual(1, live.ContentHashes.Count, "SourceId casing must not decide whether rows are superseded");
        Assert.IsTrue(live.ContentHashes.Contains("new-hash"));
        Assert.IsFalse(live.ContentHashes.Contains("old-hash"));
    }

    // ── The scheme guard (2026-09-24, D234 Step 8) ──────────────────────────────────────────
    //
    // The drop set is built from this run's ids, so a row written under a previous id scheme can
    // never be named by it. The 2026-09-24 snapshot carried 3,723 such rows over 51 bare-filename
    // ids from the pre-Zenya corpus, beside 33,223 "pdf/<guid>.pdf" rows, and a restore would have
    // written every one of them back into the index.
    [TestMethod]
    public async Task UpdateAsync_DropsRowsWrittenUnderAPreviousIdScheme()
    {
        var blobStore = new Mock<IBlobStore>();
        SetupExistingPointer(blobStore, ("2024/01/01/ts-snapshot-pdf-instance-old.json", "instance-old"));
        var previousChunks = new List<SnapshotChunk>
        {
            TestChunk.Snapshot("live-id",  "pdf/11111111-2222-3333-4444-555555555555.pdf", "Live",  "live body",  "live-hash"),
            TestChunk.Snapshot("ghost-id", "Aanbrengbonus (Versie 5).pdf",                 "Ghost", "ghost body", "ghost-hash"),
        };
        blobStore.Setup(s => s.DownloadJsonAsync<List<SnapshotChunk>>(
                It.IsAny<BlobContainerClient>(), "2024/01/01/ts-snapshot-pdf-instance-old.json", It.IsAny<CancellationToken>()))
            .ReturnsAsync(previousChunks);
        var service = BuildService(blobStore);
        var newChunks = new List<TestChunk>
        {
            new("new-id", "pdf/66666666-7777-8888-9999-000000000000.pdf", "New", null, "new content", null, 0, 0, "new-hash"),
        };

        var live = await service.UpdateAsync("pdf", newChunks, staleDocumentIds: [], processedDocumentIds: [], instanceId: "run-2", startedAt: StartedAt);

        Assert.AreEqual(1, live.ForeignSchemeRowsDropped);
        Assert.IsFalse(live.ContentHashes.Contains("ghost-hash"), "the old-scheme row must not stay live");
        Assert.IsFalse(live.DocumentIds.Contains("Aanbrengbonus (Versie 5).pdf"));
        // Its hash leaving the live set is what finally lets the vector cache evict its vector.
        Assert.IsTrue(live.ContentHashes.Contains("live-hash"));
        Assert.IsTrue(live.ContentHashes.Contains("new-hash"));
    }

    // The tripwire. A source whose ids do not carry its own name as a prefix must keep every row
    // rather than have the snapshot silently emptied - "drop everything" is never the right answer
    // to "the prefix assumption does not hold here".
    [TestMethod]
    public async Task UpdateAsync_KeepsEveryRow_WhenNoneMatchesTheSchemeAtAll()
    {
        var blobStore = new Mock<IBlobStore>();
        SetupExistingPointer(blobStore, ("2024/01/01/ts-snapshot-pdf-instance-old.json", "instance-old"));
        var previousChunks = new List<SnapshotChunk>
        {
            TestChunk.Snapshot("a", "CAO VVT (Versie 6).pdf",   "A", "a body", "hash-a"),
            TestChunk.Snapshot("b", "CAO GHZ (Versie 4).pdf",   "B", "b body", "hash-b"),
        };
        blobStore.Setup(s => s.DownloadJsonAsync<List<SnapshotChunk>>(
                It.IsAny<BlobContainerClient>(), "2024/01/01/ts-snapshot-pdf-instance-old.json", It.IsAny<CancellationToken>()))
            .ReturnsAsync(previousChunks);
        var service = BuildService(blobStore);

        var live = await service.UpdateAsync("pdf", new List<TestChunk>(), staleDocumentIds: [], processedDocumentIds: [], instanceId: "run-2", startedAt: StartedAt);

        Assert.AreEqual(0, live.ForeignSchemeRowsDropped, "nothing may be dropped when nothing matches");
        Assert.AreEqual(2, live.ContentHashes.Count);
    }

    // The prefix is the source's own name, compared case-insensitively like every other id
    // comparison on this path.
    [TestMethod]
    public async Task UpdateAsync_SchemeMatchIsCaseInsensitive()
    {
        var blobStore = new Mock<IBlobStore>();
        SetupExistingPointer(blobStore, ("2024/01/01/ts-snapshot-pdf-instance-old.json", "instance-old"));
        var previousChunks = new List<SnapshotChunk>
        {
            TestChunk.Snapshot("upper", "PDF/UPPER.pdf",        "Upper", "upper body", "hash-upper"),
            TestChunk.Snapshot("ghost", "Ontruimingsplan.pdf",  "Ghost", "ghost body", "hash-ghost"),
        };
        blobStore.Setup(s => s.DownloadJsonAsync<List<SnapshotChunk>>(
                It.IsAny<BlobContainerClient>(), "2024/01/01/ts-snapshot-pdf-instance-old.json", It.IsAny<CancellationToken>()))
            .ReturnsAsync(previousChunks);
        var service = BuildService(blobStore);

        var live = await service.UpdateAsync("pdf", new List<TestChunk>(), staleDocumentIds: [], processedDocumentIds: [], instanceId: "run-2", startedAt: StartedAt);

        Assert.AreEqual(1, live.ForeignSchemeRowsDropped);
        Assert.IsTrue(live.ContentHashes.Contains("hash-upper"));
        Assert.IsFalse(live.ContentHashes.Contains("hash-ghost"));
    }
}
