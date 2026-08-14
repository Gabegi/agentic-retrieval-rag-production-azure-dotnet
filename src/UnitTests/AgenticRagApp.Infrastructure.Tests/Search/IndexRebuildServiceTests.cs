using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Infrastructure.Clients.Search;

namespace RagApp.UnitTests.Infrastructure.Search;

// The teardown/rebuild order is the reason IndexRebuildService exists - Azure AI Search
// refuses to delete an index while a knowledge source still references it - so it gets an
// explicit test here rather than one per caller.
[TestClass]
public class IndexRebuildServiceTests
{
    private sealed class Deps
    {
        public Mock<IIndexService>     IndexService     = new();
        public Mock<IKnowledgeService> KnowledgeService = new();

        public IndexRebuildService Build() => new(
            IndexService.Object, KnowledgeService.Object, NullLogger<IndexRebuildService>.Instance);
    }

    [TestMethod]
    public async Task RecreateEmptyAsync_DeletesKnowledgeBaseAndSourceBeforeIndexThenRebuildsAfter()
    {
        var deps      = new Deps();
        var callOrder = new List<string>();
        deps.KnowledgeService.Setup(s => s.DeleteKnowledgeBaseAsync(It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("delete-base")).Returns(Task.CompletedTask);
        deps.KnowledgeService.Setup(s => s.DeleteKnowledgeSourceAsync(It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("delete-source")).Returns(Task.CompletedTask);
        deps.IndexService.Setup(s => s.RecreateIndexAsync())
            .Callback(() => callOrder.Add("recreate-index")).Returns(Task.CompletedTask);
        deps.KnowledgeService.Setup(s => s.EnsureKnowledgeSourceAsync(It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("ensure-source")).Returns(Task.CompletedTask);
        deps.KnowledgeService.Setup(s => s.EnsureKnowledgeBaseAsync(It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("ensure-base")).Returns(Task.CompletedTask);

        await deps.Build().RecreateEmptyAsync(CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { "delete-base", "delete-source", "recreate-index", "ensure-source", "ensure-base" }, callOrder);
    }

    // A teardown failure must not be swallowed into a half-rebuilt stack - the caller's
    // error handling (activity failure, HTTP 500) depends on this propagating.
    [TestMethod]
    public async Task RecreateEmptyAsync_RecreateIndexThrows_PropagatesAndSkipsRebuild()
    {
        var deps = new Deps();
        deps.KnowledgeService.Setup(s => s.DeleteKnowledgeBaseAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        deps.KnowledgeService.Setup(s => s.DeleteKnowledgeSourceAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        deps.IndexService.Setup(s => s.RecreateIndexAsync()).ThrowsAsync(new Exception("boom"));

        await Assert.ThrowsExactlyAsync<Exception>(() => deps.Build().RecreateEmptyAsync(CancellationToken.None));

        deps.KnowledgeService.Verify(s => s.EnsureKnowledgeSourceAsync(It.IsAny<CancellationToken>()), Times.Never);
        deps.KnowledgeService.Verify(s => s.EnsureKnowledgeBaseAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
