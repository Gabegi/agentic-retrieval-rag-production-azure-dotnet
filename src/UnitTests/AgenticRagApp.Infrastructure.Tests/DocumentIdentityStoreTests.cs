using System.Text;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Moq;
using AgenticRagApp.Infrastructure.Clients.DocumentIdentity;

namespace RagApp.UnitTests.Infrastructure;

// Covers the eviction path and the read/write pair around it. GetAllAsync and SetAsync are
// also exercised indirectly through DocumentIdentityResolverTests against a mocked store,
// but that never reaches this class - the blob-name encoding and the two skip-on-bad-blob
// paths only exist here.
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

    // ── GetAllAsync ──────────────────────────────────────────────────────────

    [TestMethod]
    public async Task GetAllAsync_ReturnsEveryReadableRecordUnderThePrefix()
    {
        // The whole store is the comparison set for clustering - a record silently missing
        // here is a document that cannot be matched to its own family.
        var container = new Mock<BlobContainerClient>();
        var first     = new Mock<BlobClient>();
        var second    = new Mock<BlobClient>();

        container
            .Setup(c => c.GetBlobsAsync(BlobTraits.None, BlobStates.None, "document-identity/", It.IsAny<CancellationToken>()))
            .Returns(BlobPage("document-identity/a.json", "document-identity/b.json"));
        container.Setup(c => c.GetBlobClient("document-identity/a.json")).Returns(first.Object);
        container.Setup(c => c.GetBlobClient("document-identity/b.json")).Returns(second.Object);
        first.Setup(b => b.DownloadContentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Download(Record("a.pdf")));
        second.Setup(b => b.DownloadContentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Download(Record("b.pdf")));

        var records = await new DocumentIdentityStore(container.Object).GetAllAsync();

        CollectionAssert.AreEquivalent(new[] { "a.pdf", "b.pdf" }, records.Select(r => r.SourceId).ToList());
    }

    [TestMethod]
    public async Task GetAllAsync_BlobDeletedBetweenListingAndDownload_IsSkippedRatherThanThrowing()
    {
        // The listing is a snapshot; an eviction can remove a blob before this loop reaches
        // it. That is an ordinary race, not a failed run.
        var container = new Mock<BlobContainerClient>();
        var gone      = new Mock<BlobClient>();
        var present   = new Mock<BlobClient>();

        container
            .Setup(c => c.GetBlobsAsync(BlobTraits.None, BlobStates.None, "document-identity/", It.IsAny<CancellationToken>()))
            .Returns(BlobPage("document-identity/gone.json", "document-identity/here.json"));
        container.Setup(c => c.GetBlobClient("document-identity/gone.json")).Returns(gone.Object);
        container.Setup(c => c.GetBlobClient("document-identity/here.json")).Returns(present.Object);
        gone.Setup(b => b.DownloadContentAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "BlobNotFound"));
        present.Setup(b => b.DownloadContentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Download(Record("here.pdf")));

        var records = await new DocumentIdentityStore(container.Object).GetAllAsync();

        Assert.AreEqual(1, records.Count);
        Assert.AreEqual("here.pdf", records[0].SourceId);
    }

    [TestMethod]
    public async Task GetAllAsync_NonNotFoundFailure_Propagates()
    {
        // Only the 404 race is benign. A 403 or a 500 means the store cannot be read at all,
        // and clustering against a partial corpus would quietly assign wrong families.
        var container = new Mock<BlobContainerClient>();
        var blob      = new Mock<BlobClient>();

        container
            .Setup(c => c.GetBlobsAsync(BlobTraits.None, BlobStates.None, "document-identity/", It.IsAny<CancellationToken>()))
            .Returns(BlobPage("document-identity/a.json"));
        container.Setup(c => c.GetBlobClient(It.IsAny<string>())).Returns(blob.Object);
        blob.Setup(b => b.DownloadContentAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(403, "AuthorizationFailure"));

        var store = new DocumentIdentityStore(container.Object);

        await Assert.ThrowsExactlyAsync<RequestFailedException>(() => store.GetAllAsync());
    }

    [TestMethod]
    public async Task GetAllAsync_CorruptEntry_IsSkippedAndTheRestStillLoad()
    {
        // Same reasoning as the eviction path's corrupt case, from the read side: one
        // half-written blob must not fail the whole clustering pass.
        var container = new Mock<BlobContainerClient>();
        var corrupt   = new Mock<BlobClient>();
        var good      = new Mock<BlobClient>();

        container
            .Setup(c => c.GetBlobsAsync(BlobTraits.None, BlobStates.None, "document-identity/", It.IsAny<CancellationToken>()))
            .Returns(BlobPage("document-identity/corrupt.json", "document-identity/good.json"));
        container.Setup(c => c.GetBlobClient("document-identity/corrupt.json")).Returns(corrupt.Object);
        container.Setup(c => c.GetBlobClient("document-identity/good.json")).Returns(good.Object);
        corrupt.Setup(b => b.DownloadContentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(
                BlobsModelFactory.BlobDownloadResult(content: BinaryData.FromString("{ not json")),
                Mock.Of<Response>()));
        good.Setup(b => b.DownloadContentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Download(Record("good.pdf")));

        var records = await new DocumentIdentityStore(container.Object).GetAllAsync();

        Assert.AreEqual(1, records.Count);
        Assert.AreEqual("good.pdf", records[0].SourceId);
    }

    [TestMethod]
    public async Task GetAllAsync_EmptyStore_ReturnsEmptyWithoutDownloading()
    {
        var container = new Mock<BlobContainerClient>();
        container
            .Setup(c => c.GetBlobsAsync(BlobTraits.None, BlobStates.None, "document-identity/", It.IsAny<CancellationToken>()))
            .Returns(BlobPage());

        var records = await new DocumentIdentityStore(container.Object).GetAllAsync();

        Assert.AreEqual(0, records.Count);
        container.Verify(c => c.GetBlobClient(It.IsAny<string>()), Times.Never);
    }

    // ── SetAsync ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task SetAsync_WritesUnderThePrefixWithABlobSafeEncodedSourceId()
    {
        // SourceId is a blob name, so it can contain '/' and spaces - used as a path segment
        // directly it would silently write to a nested path instead of one flat key.
        var container = new Mock<BlobContainerClient>();
        var blob      = new Mock<BlobClient>();
        string? writtenName = null;

        SetupCreateContainer(container);
        container.Setup(c => c.GetBlobClient(It.IsAny<string>()))
            .Callback<string>(n => writtenName = n)
            .Returns(blob.Object);
        SetupUpload(blob);

        await new DocumentIdentityStore(container.Object).SetAsync(Record("sub folder/doc+1.pdf"));

        Assert.IsNotNull(writtenName);
        StringAssert.StartsWith(writtenName, "document-identity/");
        StringAssert.EndsWith(writtenName, ".json");

        // Everything between the prefix and the extension is URL-safe base64 of the SourceId -
        // no '/' to make a nested path, no '+' to be mangled by a URL round-trip.
        var key = writtenName!.Substring("document-identity/".Length);
        key = key.Substring(0, key.Length - ".json".Length);
        Assert.IsFalse(key.Contains('/'));
        Assert.IsFalse(key.Contains('+'));
        Assert.AreEqual(
            "sub folder/doc+1.pdf",
            Encoding.UTF8.GetString(Convert.FromBase64String(key.Replace('-', '+').Replace('_', '/'))));
    }

    [TestMethod]
    public async Task SetAsync_CreatesTheContainerBeforeUploading()
    {
        // The identity store is written on the first run of a fresh deployment, before
        // anything else has created the artifacts container.
        var container = new Mock<BlobContainerClient>();
        var blob      = new Mock<BlobClient>();
        var sequence  = new List<string>();

        SetupCreateContainer(container, () => sequence.Add("create"));
        container.Setup(c => c.GetBlobClient(It.IsAny<string>())).Returns(blob.Object);
        SetupUpload(blob, _ => sequence.Add("upload"));

        await new DocumentIdentityStore(container.Object).SetAsync(Record("doc.pdf"));

        CollectionAssert.AreEqual(new[] { "create", "upload" }, sequence);
    }

    [TestMethod]
    public async Task SetAsync_WritesJsonThatGetAllAsyncCanReadBack()
    {
        // The two halves have to agree on the serialized shape; nothing else pins that,
        // because each side is otherwise tested against a hand-built payload.
        var container = new Mock<BlobContainerClient>();
        var blob      = new Mock<BlobClient>();
        byte[]? written = null;

        SetupCreateContainer(container);
        container.Setup(c => c.GetBlobClient(It.IsAny<string>())).Returns(blob.Object);
        SetupUpload(blob, bytes => written = bytes);

        await new DocumentIdentityStore(container.Object).SetAsync(Record("doc.pdf"));

        Assert.IsNotNull(written);
        var roundTripped = JsonSerializer.Deserialize<DocumentIdentityRecord>(written);
        Assert.AreEqual("doc.pdf", roundTripped!.SourceId);
        Assert.AreEqual(ModelId, roundTripped.EmbeddingModelId);
    }

    private static void SetupCreateContainer(Mock<BlobContainerClient> container, Action? onCreate = null) =>
        container.Setup(c => c.CreateIfNotExistsAsync(
                It.IsAny<PublicAccessType>(), It.IsAny<IDictionary<string, string>>(),
                It.IsAny<BlobContainerEncryptionScopeOptions>(), It.IsAny<CancellationToken>()))
            .Callback(() => onCreate?.Invoke())
            .ReturnsAsync(Response.FromValue(BlobsModelFactory.BlobContainerInfo(default, default), Mock.Of<Response>()));

    private static void SetupUpload(Mock<BlobClient> blob, Action<byte[]>? onUpload = null) =>
        blob.Setup(b => b.UploadAsync(It.IsAny<Stream>(), true, It.IsAny<CancellationToken>()))
            .Callback<Stream, bool, CancellationToken>((s, _, _) =>
            {
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                onUpload?.Invoke(ms.ToArray());
            })
            .ReturnsAsync(Response.FromValue(
                BlobsModelFactory.BlobContentInfo(default, default, null, null, 0), Mock.Of<Response>()));
}
