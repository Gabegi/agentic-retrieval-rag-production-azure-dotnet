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

    // The on-disk format since 2026-09-18 (D203 O2): raw little-endian float32, 4 bytes each.
    private static Response<BlobDownloadResult> DownloadResult(params float[] vector)
    {
        var bytes  = System.Runtime.InteropServices.MemoryMarshal.AsBytes(vector.AsSpan()).ToArray();
        var result = BlobsModelFactory.BlobDownloadResult(content: BinaryData.FromBytes(bytes));
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

    // A body that is not a whole number of floats is corrupt or half-written: a miss, so the
    // caller re-embeds and overwrites it, rather than a failed run over one bad blob. Same policy
    // the JSON format had for a JsonException; the JSON text itself is now such a body.
    [TestMethod]
    public async Task TryGetAsync_BodyNotAWholeNumberOfFloats_ReturnsNullInsteadOfThrowing()
    {
        var (cache, _, blob) = BuildCache();
        blob.Setup(b => b.DownloadContentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(DownloadResult("[1,2,3.5]"));   // 9 bytes - the old format, unreadable now

        var result = await cache.TryGetAsync("hash1");

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task TryGetAsync_EmptyBody_ReturnsNull()
    {
        var (cache, _, blob) = BuildCache();
        blob.Setup(b => b.DownloadContentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(DownloadResult(""));

        Assert.IsNull(await cache.TryGetAsync("hash1"));
    }

    [TestMethod]
    public async Task TryGetAsync_Float32Body_ReturnsVector()
    {
        var (cache, _, blob) = BuildCache();
        blob.Setup(b => b.DownloadContentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(DownloadResult(1f, 2f, 3.5f));

        var result = await cache.TryGetAsync("hash1");

        CollectionAssert.AreEqual(new float[] { 1, 2, 3.5f }, result!.Vector);
    }

    // Write then read through the same encoding, on a vector with the awkward values - the
    // property the format has to hold is that what comes back is bit-identical to what went in.
    [TestMethod]
    public async Task SetAsync_ThenTryGetAsync_RoundTripsBitExactly()
    {
        var (cache, _, blob) = BuildCache();
        byte[]? uploaded = null;
        blob.Setup(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback((Stream s, bool _, CancellationToken _) => { using var ms = new MemoryStream(); s.CopyTo(ms); uploaded = ms.ToArray(); })
            .ReturnsAsync((Response<BlobContentInfo>)null!);
        var original = new[] { 0f, -0f, 1e-45f, float.MaxValue, float.Epsilon, -0.0123456789f, 3072.5f };

        var written = await cache.SetAsync("hash1", original);
        blob.Setup(b => b.DownloadContentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(BlobsModelFactory.BlobDownloadResult(content: BinaryData.FromBytes(uploaded!)), Mock.Of<Response>()));
        var read = await cache.TryGetAsync("hash1");

        Assert.AreEqual(original.Length * 4, written);
        Assert.AreEqual(original.Length * 4, read!.Bytes);
        for (var i = 0; i < original.Length; i++)
            Assert.AreEqual(BitConverter.SingleToInt32Bits(original[i]), BitConverter.SingleToInt32Bits(read.Vector[i]), $"component {i}");
    }

    [TestMethod]
    public async Task TryGetAsync_UsesContentHashScopedBlobPath()
    {
        var (cache, container, blob) = BuildCache();
        blob.Setup(b => b.DownloadContentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(DownloadResult(1f));

        await cache.TryGetAsync("abc123");

        container.Verify(c => c.GetBlobClient("vector-cache/abc123.f32"), Times.Once);
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

        container.Verify(c => c.GetBlobClient("vector-cache/hash1.f32"), Times.Once);
        blob.Verify(b => b.UploadAsync(It.IsAny<Stream>(), true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task EvictOrphanedAsync_DeletesBlobsNotInLiveHashes_KeepsTheRest()
    {
        var (cache, container, blob) = BuildCache();
        var deletedNames = new List<string>();
        container.Setup(c => c.GetBlobsAsync(BlobTraits.None, BlobStates.None, "vector-cache/", It.IsAny<CancellationToken>()))
            .Returns(BlobPage("vector-cache/live.f32", "vector-cache/orphan.f32"));
        container.Setup(c => c.GetBlobClient(It.IsAny<string>()))
            .Returns((string name) =>
            {
                deletedNames.Add(name);
                return blob.Object;
            });
        blob.Setup(b => b.DeleteIfExistsAsync(
                It.IsAny<DeleteSnapshotsOption>(), It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(true, Mock.Of<Response>()));

        var eviction = await cache.EvictOrphanedAsync(new HashSet<string> { "live" });

        Assert.AreEqual(1, eviction.Deleted);
        CollectionAssert.Contains(deletedNames, "vector-cache/orphan.f32");
        CollectionAssert.DoesNotContain(deletedNames, "vector-cache/live.f32");
    }

    [TestMethod]
    public async Task EvictOrphanedAsync_NoOrphans_ReturnsZeroWithoutDeleting()
    {
        var (cache, container, blob) = BuildCache();
        container.Setup(c => c.GetBlobsAsync(BlobTraits.None, BlobStates.None, "vector-cache/", It.IsAny<CancellationToken>()))
            .Returns(BlobPage("vector-cache/live.f32"));

        var eviction = await cache.EvictOrphanedAsync(new HashSet<string> { "live" });

        Assert.AreEqual(0, eviction.Deleted);
        blob.Verify(b => b.DeleteIfExistsAsync(
            It.IsAny<DeleteSnapshotsOption>(), It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // --- What a read and an eviction measure about themselves (2026-09-18, D203 §3) ---

    // Bytes is the blob's content length as downloaded - what a hit weighs. Three floats are
    // twelve bytes in the raw format (D203 O2); a real 3,072-float entry is 12,288.
    [TestMethod]
    public async Task TryGetAsync_ReportsTheBytesDownloaded()
    {
        var (cache, _, blob) = BuildCache();
        blob.Setup(b => b.DownloadContentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(DownloadResult(1f, 2f, 3.5f));

        var result = await cache.TryGetAsync("hash1");

        Assert.AreEqual(12, result!.Bytes);
        Assert.IsTrue(result.DeserializeTicks >= 0);
    }

    [TestMethod]
    public async Task SetAsync_ReturnsTheSerializedBytesWritten()
    {
        var (cache, _, blob) = BuildCache();
        blob.Setup(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Response<BlobContentInfo>)null!);

        var bytes = await cache.SetAsync("hash1", [1, 2, 3]);

        Assert.AreEqual(3 * 4, bytes, "raw float32: four bytes per component (D203 O2)");
    }

    // Listed is every blob under the prefix, live or not - the cache's size on disk in entries.
    // The size figures come from the listing's content lengths: median of {10, 20, 30} is 20 and
    // the total 60. A listing that carries no sizes (BlobItem without properties, as the other
    // tests here build) reports both as null rather than 0 - not measured, not empty.
    [TestMethod]
    public async Task EvictOrphanedAsync_ReportsListedCountAndBlobSizesFromTheListing()
    {
        var (cache, container, blob) = BuildCache();
        container.Setup(c => c.GetBlobsAsync(BlobTraits.None, BlobStates.None, "vector-cache/", It.IsAny<CancellationToken>()))
            .Returns(BlobPageWithSizes(("vector-cache/a.f32", 30), ("vector-cache/b.f32", 10), ("vector-cache/c.f32", 20)));
        blob.Setup(b => b.DeleteIfExistsAsync(
                It.IsAny<DeleteSnapshotsOption>(), It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(true, Mock.Of<Response>()));

        var eviction = await cache.EvictOrphanedAsync(new HashSet<string> { "a" });

        Assert.AreEqual(3, eviction.Listed);
        Assert.AreEqual(2, eviction.Deleted);
        Assert.AreEqual(60L, eviction.BlobBytesTotal);
        Assert.AreEqual(20L, eviction.BlobBytesP50);
        Assert.IsTrue(eviction.ListMs >= 0 && eviction.DeleteMs >= 0);
        Assert.AreEqual(2, eviction.DeleteLatency!.Count, "one latency sample per delete (D203 §6c)");
    }

    // Deletes fan out MaxCacheParallelism-wide since 2026-09-18 (D203 O3). Every orphan is still
    // deleted exactly once and nothing live is touched, whatever the width; the name collector
    // is concurrent because the callback now runs from several probes at once.
    [TestMethod]
    public async Task EvictOrphanedAsync_ManyOrphans_DeletesEachOnceAndKeepsEveryLiveEntry()
    {
        var (cache, container, blob) = BuildCache();
        var deletedNames = new System.Collections.Concurrent.ConcurrentBag<string>();
        var names = Enumerable.Range(0, 20).Select(i => $"vector-cache/orphan{i}.f32")
            .Concat(["vector-cache/live1.f32", "vector-cache/live2.f32"]).ToArray();
        container.Setup(c => c.GetBlobsAsync(BlobTraits.None, BlobStates.None, "vector-cache/", It.IsAny<CancellationToken>()))
            .Returns(BlobPage(names));
        container.Setup(c => c.GetBlobClient(It.IsAny<string>()))
            .Returns((string name) => { deletedNames.Add(name); return blob.Object; });
        blob.Setup(b => b.DeleteIfExistsAsync(
                It.IsAny<DeleteSnapshotsOption>(), It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(true, Mock.Of<Response>()));

        var eviction = await cache.EvictOrphanedAsync(new HashSet<string> { "live1", "live2" });

        Assert.AreEqual(22, eviction.Listed);
        Assert.AreEqual(20, eviction.Deleted);
        Assert.AreEqual(20, eviction.DeleteLatency!.Count);
        Assert.AreEqual(20, deletedNames.Distinct().Count(), "each orphan deleted once");
        Assert.IsFalse(deletedNames.Any(n => n.Contains("live")), "no live entry touched");
    }

    // The JSON-era `.json` entries are swept whether or not their hash is live (D203 O2): the
    // format is unreadable now and the vector is re-embedded and rewritten as `.f32` on the same
    // run. Counted apart from orphans so ChunksEvicted keeps meaning "orphans"; sizes come from
    // `.f32` entries only, so a listing of legacy blobs alone reports no sizes. A name in neither
    // format is left alone and not counted as anything but listed.
    [TestMethod]
    public async Task EvictOrphanedAsync_LegacyJsonEntries_AreSweptEvenWhenLive_AndCountedApart()
    {
        var (cache, container, blob) = BuildCache();
        var deletedNames = new System.Collections.Concurrent.ConcurrentBag<string>();
        container.Setup(c => c.GetBlobsAsync(BlobTraits.None, BlobStates.None, "vector-cache/", It.IsAny<CancellationToken>()))
            .Returns(BlobPageWithSizes(
                ("vector-cache/live.json",   38_945),   // legacy, hash live  -> swept
                ("vector-cache/gone.json",   38_945),   // legacy, hash gone  -> swept
                ("vector-cache/live.f32",    12_288),   // current, live      -> kept
                ("vector-cache/orphan.f32",  12_288),   // current, orphan    -> evicted
                ("vector-cache/README.txt",     100))); // neither format     -> ignored
        container.Setup(c => c.GetBlobClient(It.IsAny<string>()))
            .Returns((string name) => { deletedNames.Add(name); return blob.Object; });
        blob.Setup(b => b.DeleteIfExistsAsync(
                It.IsAny<DeleteSnapshotsOption>(), It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(true, Mock.Of<Response>()));

        var eviction = await cache.EvictOrphanedAsync(new HashSet<string> { "live" });

        Assert.AreEqual(5, eviction.Listed);
        Assert.AreEqual(1, eviction.Deleted,       "orphans only");
        Assert.AreEqual(2, eviction.LegacyDeleted, "both .json entries, live or not");
        Assert.AreEqual(3, eviction.DeleteLatency!.Count, "every delete made, whichever bucket");
        Assert.AreEqual(12_288L, eviction.BlobBytesP50, "sizes from .f32 entries only");
        Assert.AreEqual(24_576L, eviction.BlobBytesTotal);
        CollectionAssert.AreEquivalent(
            new[] { "vector-cache/live.json", "vector-cache/gone.json", "vector-cache/orphan.f32" },
            deletedNames.Distinct().ToArray());
    }

    [TestMethod]
    public async Task EvictOrphanedAsync_ListingWithoutSizes_ReportsSizesAsNull()
    {
        var (cache, container, _) = BuildCache();
        container.Setup(c => c.GetBlobsAsync(BlobTraits.None, BlobStates.None, "vector-cache/", It.IsAny<CancellationToken>()))
            .Returns(BlobPage("vector-cache/live.f32"));

        var eviction = await cache.EvictOrphanedAsync(new HashSet<string> { "live" });

        Assert.AreEqual(1, eviction.Listed);
        Assert.IsNull(eviction.BlobBytesTotal);
        Assert.IsNull(eviction.BlobBytesP50);
        Assert.IsNull(eviction.DeleteLatency, "nothing deleted, so no delete latency - null, not zero");
    }

    private static AsyncPageable<BlobItem> BlobPageWithSizes(params (string Name, long Size)[] blobs)
    {
        var items = blobs
            .Select(b => BlobsModelFactory.BlobItem(
                name: b.Name,
                properties: BlobsModelFactory.BlobItemProperties(accessTierInferred: false, contentLength: b.Size)))
            .ToList();
        var page = Page<BlobItem>.FromValues(items, continuationToken: null, response: Mock.Of<Response>());
        return AsyncPageable<BlobItem>.FromPages([page]);
    }

    private static AsyncPageable<BlobItem> BlobPage(params string[] names)
    {
        var items    = names.Select(n => BlobsModelFactory.BlobItem(name: n)).ToList();
        var page     = Page<BlobItem>.FromValues(items, continuationToken: null, response: Mock.Of<Response>());
        return AsyncPageable<BlobItem>.FromPages([page]);
    }
}
