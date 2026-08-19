using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Moq;
using AgenticRagApp.Infrastructure.Clients.Blob;
using AgenticRagApp.Infrastructure.Clients.Search;
using AgenticRagApp.Infrastructure.Configuration;

namespace RagApp.UnitTests.Infrastructure;

// The pointer this reads decides which index every query and every upload actually talks to,
// so the interesting cases are all the ones where the pointer is NOT a clean value: absent,
// blank, or unreadable. Each must land on the configured base name - "generation zero" -
// rather than on an empty index name, which Azure Search would reject at call time with an
// error naming the SDK rather than the pointer that caused it.
[TestClass]
public class CurrentIndexNameProviderTests
{
    private const string PointerPath = "indexing/_current-index-name.json";
    private const string ConfiguredName = "base-index";

    // IndexNamePointer is private to CurrentIndexNameProvider, so the generic read cannot be
    // set up by name through Moq the way SnapshotService.SnapshotPointer can. This fake
    // answers TryReadJsonWithETagAsync<T> by deserializing a JSON payload into whatever T the
    // provider asked for, which is exactly what the real store does over the wire.
    private sealed class FakeBlobStore : IBlobStore
    {
        private readonly string? _pointerJson;
        private readonly Exception? _readThrows;

        public FakeBlobStore(string? pointerJson = null, Exception? readThrows = null)
        {
            _pointerJson = pointerJson;
            _readThrows  = readThrows;
        }

        public string? RequestedPath { get; private set; }
        public string? UploadedPath { get; private set; }
        public object? UploadedValue { get; private set; }
        public int AssertContainerExistsCalls { get; private set; }

        public Task<(T? Value, ETag? ETag)> TryReadJsonWithETagAsync<T>(
            BlobContainerClient container, string blobName, CancellationToken ct = default)
        {
            RequestedPath = blobName;
            if (_readThrows is not null) throw _readThrows;
            return Task.FromResult(_pointerJson is null
                ? (default(T), (ETag?)null)
                : (JsonSerializer.Deserialize<T>(_pointerJson), (ETag?)null));
        }

        public Task AssertContainerExistsAsync(BlobContainerClient container, CancellationToken ct = default)
        {
            AssertContainerExistsCalls++;
            return Task.CompletedTask;
        }

        public Task UploadJsonAsync<T>(BlobContainerClient container, string blobName, T value,
            JsonSerializerOptions? options = null, CancellationToken ct = default)
        {
            UploadedPath  = blobName;
            UploadedValue = value;
            return Task.CompletedTask;
        }

        // Not reachable from CurrentIndexNameProvider - throwing beats returning a plausible
        // default, which would let a future call slip through unnoticed.
        public Task<byte[]> DownloadBytesAsync(BlobContainerClient c, string b, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(BlobContainerClient c, string b, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> ExistsAsync(BlobContainerClient c, string b, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UploadAsync(BlobContainerClient c, string b, BinaryData d, bool o, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeleteIfExistsAsync(BlobContainerClient c, string b, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<(string Name, DateTimeOffset? LastModified, long? ContentLength, IReadOnlyDictionary<string, string> Metadata)>>
            ListBlobsAsync(BlobContainerClient c, string? prefix = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<T> DownloadJsonAsync<T>(BlobContainerClient c, string b, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> SaveJsonWithETagAsync<T>(BlobContainerClient c, string b, T v, ETag? e, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static CurrentIndexNameProvider Build(FakeBlobStore store) =>
        new(store, new Mock<BlobContainerClient>().Object, new IndexerConfig { SearchIndexName = ConfiguredName });

    [TestMethod]
    public async Task GetCurrentIndexNameAsync_PointerExists_ReturnsTheNameItNames()
    {
        var store = new FakeBlobStore("{\"IndexName\":\"base-index-v3\"}");

        var name = await Build(store).GetCurrentIndexNameAsync();

        Assert.AreEqual("base-index-v3", name);
    }

    [TestMethod]
    public async Task GetCurrentIndexNameAsync_NoPointerYet_FallsBackToTheConfiguredName()
    {
        // First deploy, or an environment that predates generations - the configured name is
        // generation zero, not an error.
        var name = await Build(new FakeBlobStore()).GetCurrentIndexNameAsync();

        Assert.AreEqual(ConfiguredName, name);
    }

    [TestMethod]
    public async Task GetCurrentIndexNameAsync_PointerWithBlankName_FallsBackRatherThanReturningBlank()
    {
        // A blank name is worse than a missing pointer: it reaches Azure Search as an empty
        // index name and fails there, pointing at the SDK rather than at this blob.
        var store = new FakeBlobStore("{\"IndexName\":\"   \"}");

        var name = await Build(store).GetCurrentIndexNameAsync();

        Assert.AreEqual(ConfiguredName, name);
    }

    [TestMethod]
    public async Task GetCurrentIndexNameAsync_ReadThrows_FallsBackInsteadOfFailingTheCaller()
    {
        // A corrupt or unreadable pointer must not take down indexing and querying with it.
        var store = new FakeBlobStore(readThrows: new RequestFailedException(500, "Storage is having a day"));

        var name = await Build(store).GetCurrentIndexNameAsync();

        Assert.AreEqual(ConfiguredName, name);
    }

    [TestMethod]
    public async Task GetCurrentIndexNameAsync_ReadsTheOneAppWidePointerPath()
    {
        // Deliberately not source-scoped: PDF and CSV chunks share one index, so there is one
        // pointer for the whole app. A per-source path here would give the two pipelines
        // different answers about which index is live.
        var store = new FakeBlobStore("{\"IndexName\":\"x\"}");

        await Build(store).GetCurrentIndexNameAsync();

        Assert.AreEqual(PointerPath, store.RequestedPath);
    }

    [TestMethod]
    public async Task SetCurrentIndexNameAsync_AssertsTheContainerThenWritesThePointer()
    {
        var store = new FakeBlobStore();

        await Build(store).SetCurrentIndexNameAsync("base-index-v4");

        Assert.AreEqual(1, store.AssertContainerExistsCalls);
        Assert.AreEqual(PointerPath, store.UploadedPath);
    }

    [TestMethod]
    public async Task SetCurrentIndexNameAsync_WritesAPointerGetCurrentReadsBack()
    {
        // Writer and reader agree on the serialized shape - the round trip is the contract,
        // and neither side alone would catch a rename of the property.
        var writeStore = new FakeBlobStore();
        await Build(writeStore).SetCurrentIndexNameAsync("base-index-v5");

        var readStore = new FakeBlobStore(JsonSerializer.Serialize(writeStore.UploadedValue));

        Assert.AreEqual("base-index-v5", await Build(readStore).GetCurrentIndexNameAsync());
    }
}
