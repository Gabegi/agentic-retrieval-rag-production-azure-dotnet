using Azure;
using Azure.Search.Documents;
using Moq;
using AgenticRagApp.Infrastructure.Clients.Search;

namespace RagApp.UnitTests.Infrastructure;

// A SearchClient is expensive to build and safe to share, so this provider caches one per
// index name. The cache is the whole point of the type - and the reason it is keyed by name
// rather than held as a single field is that the name changes under it when a restore
// promotes a new index generation.
[TestClass]
public class CurrentSearchClientProviderTests
{
    private static SearchClient Client(string indexName) =>
        new(new Uri("https://search.example.net"), indexName, new AzureKeyCredential("not-a-real-key"));

    private static Mock<ICurrentIndexNameProvider> NameProvider(params string[] namesInOrder)
    {
        var mock  = new Mock<ICurrentIndexNameProvider>();
        var queue = new Queue<string>(namesInOrder);
        mock.Setup(p => p.GetCurrentIndexNameAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => queue.Count > 1 ? queue.Dequeue() : queue.Peek());
        return mock;
    }

    [TestMethod]
    public async Task GetClientAsync_BuildsAClientForTheCurrentIndexName()
    {
        var provider = new CurrentSearchClientProvider(NameProvider("index-v1").Object, Client);

        var client = await provider.GetClientAsync();

        Assert.AreEqual("index-v1", client.IndexName);
    }

    [TestMethod]
    public async Task GetClientAsync_SameIndexNameTwice_ReusesTheCachedClient()
    {
        var built = 0;
        var provider = new CurrentSearchClientProvider(
            NameProvider("index-v1").Object,
            name => { built++; return Client(name); });

        var first  = await provider.GetClientAsync();
        var second = await provider.GetClientAsync();

        Assert.AreSame(first, second);
        Assert.AreEqual(1, built);
    }

    [TestMethod]
    public async Task GetClientAsync_IndexNameChanges_BuildsAClientForTheNewIndex()
    {
        // What happens when a restore promotes a new generation mid-process: the cached
        // client for the old name must not keep answering, or writes land in the index that
        // was just replaced.
        var provider = new CurrentSearchClientProvider(NameProvider("index-v1", "index-v2").Object, Client);

        var before = await provider.GetClientAsync();
        var after  = await provider.GetClientAsync();

        Assert.AreEqual("index-v1", before.IndexName);
        Assert.AreEqual("index-v2", after.IndexName);
        Assert.AreNotSame(before, after);
    }

    [TestMethod]
    public async Task GetClientAsync_NameReverts_ServesTheOriginalCachedClientAgain()
    {
        // The cache is keyed by name rather than "last one wins", so a rollback to the
        // previous generation costs nothing.
        var provider = new CurrentSearchClientProvider(NameProvider("index-v1", "index-v2", "index-v1").Object, Client);

        var first  = await provider.GetClientAsync();
        await provider.GetClientAsync();
        var back   = await provider.GetClientAsync();

        Assert.AreSame(first, back);
    }

    [TestMethod]
    public async Task GetClientAsync_PassesTheCancellationTokenToTheNameLookup()
    {
        using var cts = new CancellationTokenSource();
        var names = NameProvider("index-v1");
        var provider = new CurrentSearchClientProvider(names.Object, Client);

        await provider.GetClientAsync(cts.Token);

        names.Verify(p => p.GetCurrentIndexNameAsync(cts.Token), Times.Once);
    }
}
