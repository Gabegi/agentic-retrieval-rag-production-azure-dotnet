using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging.Abstractions;
using AgenticRagApp.Infrastructure.Clients.Blob;
using AgenticRagApp.Infrastructure.Clients.Search;
using Moq;
using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Services;
using AgenticRagApp.Infrastructure.Configuration;
using AgenticRagApp.Observability.Reports;

namespace RagApp.UnitTests.Indexing;

[TestClass]
public class RestoreServiceTests
{
    private static IndexerConfig Config() => new()
    {
        SearchEndpoint            = "https://search.example.com",
        OpenAiEndpoint            = "https://openai.example.com",
        OpenAiEmbeddingDeployment = "embed-deployment",
        StorageAccountUrl         = "https://storage.example.com",
        StorageContainer          = "container",
        SearchIndexName           = "my-index",
        KnowledgeSourceName       = "ks",
        KnowledgeBaseName         = "kb",
        OpenAiGptDeployment       = "gpt",
        OpenAiGptModelName        = "gpt-model",
        OpenAiEmbeddingModelName  = "text-embedding-3-large",
    };

    private static Mock<ISnapshotService> MockSnapshotService(IReadOnlyList<SnapshotChunk> chunks, string? instanceId)
    {
        var mock = new Mock<ISnapshotService>();
        mock.Setup(m => m.ReadLatestAsync("pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync((chunks, instanceId));
        return mock;
    }

    private static Mock<IVectorCache> MockVectorCache(Dictionary<string, float[]>? vectorsByHash = null)
    {
        var mock = new Mock<IVectorCache>();
        mock.Setup(m => m.TryGetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string hash, CancellationToken _) =>
                vectorsByHash is not null && vectorsByHash.TryGetValue(hash, out var v) ? new CachedVector(v) : null);
        return mock;
    }

    private static Mock<IUploadService> MockUploadService(UploadResult? result = null)
    {
        var mock = new Mock<IUploadService>();
        mock.Setup(m => m.UploadDocumentsAsync(It.IsAny<IEnumerable<ChunkObject>>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<FamilyMove>>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result ?? new UploadResult(0, 0, 0, 0, null, null, []));
        return mock;
    }

    // SnapshotChunk has no optional parameters by design - the record's own comment says a
    // field added to the index schema is added here in the same change, because the nine-field
    // version of it is how a restore once rebuilt an index with no family_id and no domain_tag.
    // These tests only care about id, document and hash, so the rest is filled in here rather
    // than at three call sites that would have to be edited again on the next schema change.
    private static SnapshotChunk Snapshot(string contentHash, string id = "id1", string documentId = "doc1.pdf") =>
        new(Id:                 id,
            DocumentId:         documentId,
            Title:              "Title",
            LastModifiedDate:   null,
            Content:            "content",
            HeadingText:        null,
            PageStart:          0,
            ChildIndex:         0,
            ContentHash:        contentHash,
            Prefix:             "",
            SectionId:          null,
            SectionIndex:       0,
            Grain:              ChunkGrain.Child,
            ParentText:         null,
            HeadingPath:        null,
            HeadingDepth:       0,
            HeadingSource:      ChunkHeadingSource.None,
            HeadingLocated:     false,
            IsOverlap:          false,
            PageEnd:            0,
            FamilyId:           null,
            DomainTag:          null,
            ConfusableWith:     [],
            Population:         null,
            Language:           null,
            TokenCount:         0,
            TableCount:         0,
            HasTable:           false,
            FigureCaptions:     [],
            Hyperlinks:         [],
            Annotations:        [],
            CreatedAt:          null,
            ModDate:            null,
            PageCount:          null,
            ValidFrom:          null,
            ValidTo:            null,
            Version:            null,
            // Source-system facts (Zenya, 2026-09-21)
            SourceDocumentId:   null,
            SourceVersion:      null,
            SourceRevision:     null,
            SourceStatus:       null,
            SourceActive:       null,
            SourceTitle:        null,
            SourceLanguage:     null,
            QuickCode:          null,
            FolderPath:         null,
            FolderName:         null,
            SourceType:         null,
            SourceDocumentType: null,
            Summary:            null,
            CheckDate:          null,
            AttentionFlags:     []);

    // The live index width a restore judges cached vectors against (D201). 2 here, matching the
    // two-component vectors these tests cache.
    private static Mock<IIndexService> MockIndexService(int dims = 2, bool fieldPresent = true)
    {
        var mock = new Mock<IIndexService>();
        mock.Setup(m => m.ReadVectorConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IndexVectorConfig(
                IndexName: "my-index", FieldName: "content_vector", FieldPresent: fieldPresent,
                Dimensions: fieldPresent ? dims : null, ConfiguredDimensions: dims,
                Algorithm: "hnsw", Metric: "cosine", M: 4, EfConstruction: 400, EfSearch: 500,
                Compression: null, Vectorizer: null, VectorizerModel: null, VectorizerDeployment: null,
                ConfiguredModelName: "text-embedding-3-large", ReadAtUtc: DateTimeOffset.UtcNow));
        return mock;
    }

    // The live source listing the restore reconciles against (2026-09-24, D234 Step 8). By
    // default it returns every document id the snapshot mentions, so the existing tests keep
    // testing what they were written to test; a test that wants the reconcile to bite passes its
    // own listing.
    private static Mock<IBlobStore> MockSourceListing(params string[] names)
    {
        var mock = new Mock<IBlobStore>();
        mock.Setup(m => m.ListBlobsAsync(It.IsAny<BlobContainerClient>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(names
                .Select(n => (Name: n, LastModified: (DateTimeOffset?)DateTimeOffset.UnixEpoch,
                              ContentLength: (long?)1, Metadata: (IReadOnlyDictionary<string, string>)new Dictionary<string, string>()))
                .ToList());
        return mock;
    }

    private static RestoreService BuildService(
        Mock<ISnapshotService> snapshotService, Mock<IVectorCache> vectorCache, Mock<IUploadService> uploadService,
        Mock<IBlobStore>? sourceListing = null) =>
        new(snapshotService.Object,
            new Mock<BlobContainerClient>().Object,
            (sourceListing ?? MockSourceListing("pdf/doc-1.pdf", "pdf/doc-2.pdf", "doc1.pdf", "doc2.pdf", "doc-a.pdf", "doc-b.pdf")).Object,
            vectorCache.Object, uploadService.Object, MockIndexService().Object, Config(), NullLogger<RestoreService>.Instance);

    [TestMethod]
    public async Task RestoreFromLatestSnapshotAsync_NoSnapshotExists_ReturnsZeroRestoredWithoutUploading()
    {
        var snapshotService = MockSnapshotService([], null);
        var vectorCache      = MockVectorCache();
        var uploadService    = MockUploadService();
        var service          = BuildService(snapshotService, vectorCache, uploadService);

        var result = await service.RestoreFromLatestSnapshotAsync();

        Assert.AreEqual(0, result.ChunksRestored);
        Assert.IsNull(result.SnapshotInstanceId);
        uploadService.Verify(u => u.UploadDocumentsAsync(
            It.IsAny<IEnumerable<ChunkObject>>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<FamilyMove>>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task RestoreFromLatestSnapshotAsync_ChunksWithCachedVectors_AreUploadedWithVectorsAttached()
    {
        var chunk = Snapshot("hash1");
        var snapshotService = MockSnapshotService([chunk], "instance-1");
        var vectorCache      = MockVectorCache(new() { ["hash1"] = [0.1f, 0.2f] });
        var uploadService    = MockUploadService(new UploadResult(1, 0, 0, 0, 42, 1024, []));
        var service          = BuildService(snapshotService, vectorCache, uploadService);

        List<ChunkObject>? uploaded = null;
        uploadService.Setup(u => u.UploadDocumentsAsync(It.IsAny<IEnumerable<ChunkObject>>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<FamilyMove>>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChunkObject>, IReadOnlyList<string>, IReadOnlyList<FamilyMove>, int, bool, CancellationToken>(
                (docs, _, _, _, _, _) => uploaded = docs.ToList())
            .ReturnsAsync(new UploadResult(1, 0, 0, 0, 42, 1024, []));

        var result = await service.RestoreFromLatestSnapshotAsync();

        Assert.AreEqual("instance-1", result.SnapshotInstanceId);
        Assert.AreEqual(1, result.ChunksRestored);
        Assert.AreEqual(0, result.ChunksFailed);
        Assert.AreEqual(0, result.ChunksMissingVector);
        Assert.AreEqual(42, result.IndexDocumentCountSnapshot);
        Assert.AreEqual("my-index", result.SearchIndexName);
        Assert.AreEqual("text-embedding-3-large", result.EmbeddingModel);

        Assert.IsNotNull(uploaded);
        Assert.AreEqual(1, uploaded!.Count);
        Assert.AreEqual("doc1.pdf", uploaded[0].DocumentId);
        CollectionAssert.AreEqual(new[] { 0.1f, 0.2f }, uploaded[0].ContentVector);
    }

    [TestMethod]
    public async Task RestoreFromLatestSnapshotAsync_ChunkWithNoCachedVector_IsCountedAsMissingButStillUploaded()
    {
        var chunk = Snapshot("hash-not-cached");
        var snapshotService = MockSnapshotService([chunk], "instance-1");
        var vectorCache      = MockVectorCache(); // empty - every lookup misses
        var uploadService    = MockUploadService(new UploadResult(1, 0, 0, 0, null, null, []));
        var service          = BuildService(snapshotService, vectorCache, uploadService);

        var result = await service.RestoreFromLatestSnapshotAsync();

        Assert.AreEqual(1, result.ChunksMissingVector);
    }

    [TestMethod]
    public async Task RestoreFromLatestSnapshotAsync_UploadReportsFailures_PropagatesChunksFailed()
    {
        var chunk = Snapshot("hash1");
        var snapshotService = MockSnapshotService([chunk], "instance-1");
        var vectorCache      = MockVectorCache(new() { ["hash1"] = [0.1f, 0.2f] });
        var uploadService    = MockUploadService(new UploadResult(0, 1, 0, 0, 0, 0, []));
        var service          = BuildService(snapshotService, vectorCache, uploadService);

        var result = await service.RestoreFromLatestSnapshotAsync();

        Assert.AreEqual(1, result.ChunksFailed);
    }

    // ── The restore reconcile (2026-09-24, D234 Step 8) ─────────────────────────────────────
    //
    // A restore used to write every snapshot row into the index. On 2026-09-24 the live snapshot
    // held 3,723 rows under 51 bare-filename ids from the retired pre-Zenya corpus, with their
    // vectors still cached, so a restore would have re-created a corpus that no longer exists.
    [TestMethod]
    public async Task RestoreFromLatestSnapshotAsync_SkipsChunksWhoseDocumentIsNoLongerInTheSource()
    {
        var snapshotService = MockSnapshotService(
            [Snapshot("hash-live", id: "live-1", documentId: "pdf/live.pdf"),
             Snapshot("hash-ghost", id: "ghost-1", documentId: "Aanbrengbonus (Versie 5).pdf")],
            "instance-1");
        var vectorCache   = MockVectorCache(new Dictionary<string, float[]> { ["hash-live"] = [1f, 2f], ["hash-ghost"] = [3f, 4f] });
        var uploadService = MockUploadService(new UploadResult(1, 0, 0, 0, null, null, []));
        var service       = BuildService(snapshotService, vectorCache, uploadService,
                                         MockSourceListing("pdf/live.pdf"));

        var result = await service.RestoreFromLatestSnapshotAsync();

        Assert.AreEqual(1, result.ChunksSkippedNotInSource);
        uploadService.Verify(m => m.UploadDocumentsAsync(
            It.Is<IEnumerable<ChunkObject>>(c => c.Count() == 1 && c.Single().DocumentId == "pdf/live.pdf"),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<FamilyMove>>(),
            It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // A restore runs when something is already wrong. "The source listing failed, carry on
    // trusting the blob" is the one behaviour that turns a recovery into a corruption, so the
    // restore refuses instead - the same stance as ChunkingService's HardCut tripwire.
    [TestMethod]
    public async Task RestoreFromLatestSnapshotAsync_RefusesWhenTheSourceListingFails()
    {
        var snapshotService = MockSnapshotService([Snapshot("hash-live", documentId: "pdf/live.pdf")], "instance-1");
        var listing = new Mock<IBlobStore>();
        listing.Setup(m => m.ListBlobsAsync(It.IsAny<BlobContainerClient>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
               .ThrowsAsync(new TimeoutException("listing unavailable"));
        var uploadService = MockUploadService();
        var service = BuildService(snapshotService, MockVectorCache(), uploadService, listing);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.RestoreFromLatestSnapshotAsync());

        uploadService.Verify(m => m.UploadDocumentsAsync(
            It.IsAny<IEnumerable<ChunkObject>>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<IReadOnlyList<FamilyMove>>(), It.IsAny<int>(), It.IsAny<bool>(),
            It.IsAny<CancellationToken>()), Times.Never, "nothing may be uploaded on a refused restore");
    }

    // An empty listing is indistinguishable from a failed one from here, and restoring on it would
    // mean re-indexing the entire snapshot against a source that claims to hold nothing.
    [TestMethod]
    public async Task RestoreFromLatestSnapshotAsync_RefusesWhenTheSourceListingIsEmpty()
    {
        var snapshotService = MockSnapshotService([Snapshot("hash-live", documentId: "pdf/live.pdf")], "instance-1");
        var uploadService   = MockUploadService();
        var service = BuildService(snapshotService, MockVectorCache(), uploadService, MockSourceListing());

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.RestoreFromLatestSnapshotAsync());

        uploadService.Verify(m => m.UploadDocumentsAsync(
            It.IsAny<IEnumerable<ChunkObject>>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<IReadOnlyList<FamilyMove>>(), It.IsAny<int>(), It.IsAny<bool>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
