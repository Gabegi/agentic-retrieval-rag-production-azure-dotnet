using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Infrastructure.Clients.Blob;

namespace RagApp.UnitTests.Infrastructure.Blob;

[TestClass]
public class BlobStoreTests
{
    private const string BlobName = "state.json";

    private sealed record RunState(int CleanedRecords);

    private static (BlobStore Store, Mock<BlobContainerClient> Container, Mock<BlobClient> Blob) BuildStore()
    {
        var blob      = new Mock<BlobClient>();
        var container = new Mock<BlobContainerClient>();
        container.Setup(c => c.GetBlobClient(BlobName)).Returns(blob.Object);
        var store = new BlobStore(NullLogger<BlobStore>.Instance);
        return (store, container, blob);
    }

    // AssertContainerExistsAsync replaced the old EnsureContainerExistsAsync (CreateIfNotExistsAsync)
    // deliberately: Terraform owns every container this app writes to, and silently auto-creating
    // one on a name mismatch is exactly how pipeline-reports and pipeline-artifacts each ended up
    // with a managed container sitting empty while writes went to an unmanaged, differently-named
    // one. See ContainerNotDeclaredException.
    [TestMethod]
    public async Task AssertContainerExistsAsync_ContainerExists_DoesNotThrow()
    {
        var container = new Mock<BlobContainerClient>();
        container.Setup(c => c.ExistsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(true, Mock.Of<Response>()));
        var store = new BlobStore(NullLogger<BlobStore>.Instance);

        await store.AssertContainerExistsAsync(container.Object);
    }

    [TestMethod]
    public async Task AssertContainerExistsAsync_ContainerMissing_ThrowsNamingTheContainer()
    {
        var container = new Mock<BlobContainerClient>();
        container.Setup(c => c.Name).Returns("pipeline-artifacts");
        container.Setup(c => c.ExistsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(false, Mock.Of<Response>()));
        var store = new BlobStore(NullLogger<BlobStore>.Instance);

        var ex = await Assert.ThrowsExactlyAsync<ContainerNotDeclaredException>(
            () => store.AssertContainerExistsAsync(container.Object));

        Assert.AreEqual("pipeline-artifacts", ex.ContainerName);
        StringAssert.Contains(ex.Message, "pipeline-artifacts");
        StringAssert.Contains(ex.Message, "storage.tf");
    }

    [TestMethod]
    public async Task TryReadJsonWithETagAsync_BlobMissing_ReturnsNullValueAndETag()
    {
        var (store, container, blob) = BuildStore();
        blob.Setup(b => b.DownloadContentAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "not found"));

        var (value, etag) = await store.TryReadJsonWithETagAsync<RunState>(container.Object, BlobName);

        Assert.IsNull(value);
        Assert.IsNull(etag);
    }

    [TestMethod]
    public async Task TryReadJsonWithETagAsync_CorruptJson_TreatedAsNoBaseline()
    {
        var (store, container, blob) = BuildStore();
        var details = BlobsModelFactory.BlobDownloadDetails(eTag: new ETag("\"x\""));
        var result  = BlobsModelFactory.BlobDownloadResult(content: BinaryData.FromString("not-json-at-all"), details: details);
        blob.Setup(b => b.DownloadContentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(result, Mock.Of<Response>()));

        var (value, etag) = await store.TryReadJsonWithETagAsync<RunState>(container.Object, BlobName);

        Assert.IsNull(value);
        Assert.IsNull(etag);
    }

    [TestMethod]
    public async Task TryReadJsonWithETagAsync_ValidJson_ReturnsValueAndETag()
    {
        var (store, container, blob) = BuildStore();
        var etag    = new ETag("\"baseline\"");
        var details = BlobsModelFactory.BlobDownloadDetails(eTag: etag);
        var result  = BlobsModelFactory.BlobDownloadResult(content: BinaryData.FromString("{\"CleanedRecords\":100}"), details: details);
        blob.Setup(b => b.DownloadContentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(result, Mock.Of<Response>()));

        var (value, returnedEtag) = await store.TryReadJsonWithETagAsync<RunState>(container.Object, BlobName);

        Assert.AreEqual(100, value!.CleanedRecords);
        Assert.AreEqual(etag, returnedEtag);
    }

    // UploadJsonAsync streams via System.IO.Pipelines rather than building an intermediate
    // string/byte[] - see IBlobStore.UploadJsonAsync's own comment for the production
    // OutOfMemoryException this replaced. These tests exercise that streaming path directly
    // rather than mocking it away, since the whole point is what actually reaches the wire.
    [TestMethod]
    public async Task UploadJsonAsync_StreamedContent_RoundTripsCorrectly()
    {
        var (store, container, blob) = BuildStore();
        byte[]? captured = null;
        blob.Setup(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(async (Stream s, bool _, CancellationToken ct) =>
            {
                using var buffer = new MemoryStream();
                await s.CopyToAsync(buffer, ct);
                captured = buffer.ToArray();
                return (Response<BlobContentInfo>)null!;
            });

        await store.UploadJsonAsync(container.Object, BlobName, new RunState(42));

        Assert.IsNotNull(captured);
        var roundTripped = JsonSerializer.Deserialize<RunState>(captured);
        Assert.AreEqual(42, roundTripped!.CleanedRecords);
    }

    [TestMethod]
    public async Task UploadJsonAsync_WithOptions_UsesTheGivenSerializerOptions()
    {
        var (store, container, blob) = BuildStore();
        byte[]? captured = null;
        blob.Setup(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(async (Stream s, bool _, CancellationToken ct) =>
            {
                using var buffer = new MemoryStream();
                await s.CopyToAsync(buffer, ct);
                captured = buffer.ToArray();
                return (Response<BlobContentInfo>)null!;
            });

        var opts = new JsonSerializerOptions { WriteIndented = true };

        await store.UploadJsonAsync(container.Object, BlobName, new RunState(1), opts);

        Assert.IsNotNull(captured);
        // WriteIndented=true means the serialized bytes contain a newline - proof the options
        // parameter actually reached the serializer, not just accepted and ignored.
        StringAssert.Contains(System.Text.Encoding.UTF8.GetString(captured), "\n");
    }

    // A large payload is exactly what this streaming rewrite exists for - assert it round-trips
    // correctly rather than just trusting the plumbing on a 20-byte record. Not a memory-ceiling
    // test (that needs a real process, not a unit test), but it exercises many pipe segments
    // rather than one, which a tiny payload never would.
    [TestMethod]
    public async Task UploadJsonAsync_LargePayload_RoundTripsCorrectly()
    {
        var (store, container, blob) = BuildStore();
        byte[]? captured = null;
        blob.Setup(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(async (Stream s, bool _, CancellationToken ct) =>
            {
                using var buffer = new MemoryStream();
                await s.CopyToAsync(buffer, ct);
                captured = buffer.ToArray();
                return (Response<BlobContentInfo>)null!;
            });

        var large = Enumerable.Range(0, 50_000).Select(i => new RunState(i)).ToList();

        await store.UploadJsonAsync(container.Object, BlobName, large);

        Assert.IsNotNull(captured);
        var roundTripped = JsonSerializer.Deserialize<List<RunState>>(captured);
        Assert.AreEqual(50_000, roundTripped!.Count);
        Assert.AreEqual(49_999, roundTripped[^1].CleanedRecords);
    }

    [TestMethod]
    public async Task UploadJsonAsync_SerializationFails_FaultsRatherThanHangs()
    {
        var (store, container, blob) = BuildStore();
        blob.Setup(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(async (Stream s, bool _, CancellationToken ct) =>
            {
                // Drains the reader side, same as the real SDK would - proves the writer's
                // failure propagates through the pipe rather than the upload task waiting
                // forever on a writer that already gave up.
                using var buffer = new MemoryStream();
                await s.CopyToAsync(buffer, ct);
                return (Response<BlobContentInfo>)null!;
            });

        // A type System.Text.Json cannot serialize (a raw, unsupported reference cycle-free but
        // deliberately broken converter target isn't easy to construct inline, so use a
        // pre-cancelled token instead - simplest reliable way to force SerializeAsync to throw
        // partway through without depending on serializer internals).
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // TaskCanceledException, not the base OperationCanceledException - that's what a
        // pre-cancelled token actually produces here, and it's still a genuine fault rather
        // than a hang, which is what this test exists to prove.
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(
            () => store.UploadJsonAsync(container.Object, BlobName, new RunState(1), ct: cts.Token));
    }

    [TestMethod]
    public async Task SaveJsonWithETagAsync_NoPreviousETag_UsesIfNoneMatchAll()
    {
        var (store, container, blob) = BuildStore();
        blob.Setup(b => b.UploadAsync(It.IsAny<BinaryData>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Response<BlobContentInfo>)null!);

        var saved = await store.SaveJsonWithETagAsync(container.Object, BlobName, new RunState(1), previousETag: null);

        Assert.IsTrue(saved);
        blob.Verify(b => b.UploadAsync(
            It.IsAny<BinaryData>(), It.Is<BlobUploadOptions>(o => o.Conditions!.IfNoneMatch == ETag.All), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task SaveJsonWithETagAsync_WithPreviousETag_UsesIfMatch()
    {
        var (store, container, blob) = BuildStore();
        var etag = new ETag("\"baseline\"");
        blob.Setup(b => b.UploadAsync(It.IsAny<BinaryData>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Response<BlobContentInfo>)null!);

        var saved = await store.SaveJsonWithETagAsync(container.Object, BlobName, new RunState(1), previousETag: etag);

        Assert.IsTrue(saved);
        blob.Verify(b => b.UploadAsync(
            It.IsAny<BinaryData>(), It.Is<BlobUploadOptions>(o => o.Conditions!.IfMatch == etag), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task SaveJsonWithETagAsync_ConcurrentWriteLostRace_ReturnsFalseInsteadOfThrowing()
    {
        var (store, container, blob) = BuildStore();
        blob.Setup(b => b.UploadAsync(It.IsAny<BinaryData>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(412, "precondition failed"));

        var saved = await store.SaveJsonWithETagAsync(container.Object, BlobName, new RunState(1), previousETag: new ETag("\"x\""));

        Assert.IsFalse(saved);
    }

    // Regression test for finding #17: Azure Blob Storage treats metadata key names as
    // case-insensitive, but the SDK hands back item.Metadata as an ordinal-comparer
    // dictionary. A manual upload setting "Zenya_Document_Id" must still be found by a
    // lookup for "zenya_document_id" (ZenyaMetadata.FromBlobMetadata), not silently read
    // as "not set".
    [TestMethod]
    public async Task ListBlobsAsync_MetadataLookupIsCaseInsensitive()
    {
        var container = new Mock<BlobContainerClient>();
        var items = new[]
        {
            BlobsModelFactory.BlobItem(
                name: "doc1.pdf",
                properties: BlobsModelFactory.BlobItemProperties(accessTierInferred: false),
                metadata: new Dictionary<string, string> { ["Zenya_Document_Id"] = "abc123" }),
        };
        var page = Page<BlobItem>.FromValues(items, continuationToken: null, response: Mock.Of<Response>());
        container.Setup(c => c.GetBlobsAsync(BlobTraits.Metadata, BlobStates.None, null, It.IsAny<CancellationToken>()))
            .Returns(AsyncPageable<BlobItem>.FromPages([page]));
        var store = new BlobStore(NullLogger<BlobStore>.Instance);

        var blobs = await store.ListBlobsAsync(container.Object);

        Assert.AreEqual(1, blobs.Count);
        Assert.IsTrue(blobs[0].Metadata.TryGetValue("zenya_document_id", out var value));
        Assert.AreEqual("abc123", value);
    }
}
