using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Functions;
using AgenticRagApp.Infrastructure.Clients.Search;
using AgenticRagApp.Infrastructure.Configuration;

namespace RagApp.UnitTests.Functions;

[TestClass]
public class IndexAdminFunctionTests
{
    private sealed class Deps
    {
        public Mock<IIndexRebuildService> IndexRebuildService = new();
        public Mock<IKnowledgeService>    KnowledgeService    = new();

        // Real config, not a mock: FullIndexRecreation compares ?confirm= against
        // SearchIndexName, so the value has to be readable rather than default-null.
        public IndexerConfig Config = new() { SearchIndexName = "test-index" };

        public IndexAdminFunction Build() => new(
            IndexRebuildService.Object, KnowledgeService.Object, Config,
            NullLogger<IndexAdminFunction>.Instance);
    }

    // ── RunFullIndexRecreation ───────────────────────────────────────────────

    // The ?confirm= guard is the whole safety mechanism on a destructive, irreversible
    // endpoint - a mismatch must not reach IndexRebuildService at all.
    [TestMethod]
    public async Task RunFullIndexRecreation_ConfirmDoesNotMatchIndexName_RefusesWithoutRecreating()
    {
        var deps     = new Deps();
        var function = deps.Build();
        var context  = new FakeFunctionContext();
        var request  = new FakeHttpRequestData(context, "", query: "confirm=wrong-index");

        var response = (FakeHttpResponseData)await function.RunFullIndexRecreation(request, context);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        deps.IndexRebuildService.Verify(s => s.RecreateEmptyAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task RunFullIndexRecreation_ConfirmMatchesIndexName_RecreatesAndReturnsOk()
    {
        var deps = new Deps();
        deps.IndexRebuildService.Setup(s => s.RecreateEmptyAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var function = deps.Build();
        var context  = new FakeFunctionContext();
        var request  = new FakeHttpRequestData(context, "", query: "confirm=test-index");

        var response = (FakeHttpResponseData)await function.RunFullIndexRecreation(request, context);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        deps.IndexRebuildService.Verify(s => s.RecreateEmptyAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task RunFullIndexRecreation_RecreateThrows_ReturnsInternalServerError()
    {
        var deps = new Deps();
        deps.IndexRebuildService.Setup(s => s.RecreateEmptyAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new Exception("boom"));
        var function = deps.Build();
        var context  = new FakeFunctionContext();
        var request  = new FakeHttpRequestData(context, "", query: "confirm=test-index");

        var response = (FakeHttpResponseData)await function.RunFullIndexRecreation(request, context);

        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // ── RunSetupKnowledgeBase ────────────────────────────────────────────────

    [TestMethod]
    public async Task RunSetupKnowledgeBase_EnsuresKnowledgeSourceAndBase_ReturnsOk()
    {
        var deps = new Deps();
        deps.KnowledgeService.Setup(s => s.EnsureKnowledgeSourceAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        deps.KnowledgeService.Setup(s => s.EnsureKnowledgeBaseAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var function = deps.Build();
        var context  = new FakeFunctionContext();
        var request  = new FakeHttpRequestData(context, "");

        var response = (FakeHttpResponseData)await function.RunSetupKnowledgeBase(request, context);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        deps.KnowledgeService.Verify(s => s.EnsureKnowledgeSourceAsync(It.IsAny<CancellationToken>()), Times.Once);
        deps.KnowledgeService.Verify(s => s.EnsureKnowledgeBaseAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
