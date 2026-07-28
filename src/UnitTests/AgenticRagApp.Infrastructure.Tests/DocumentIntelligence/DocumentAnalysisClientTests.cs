using Azure;
using Azure.AI.DocumentIntelligence;
using Moq;
using AgenticRagApp.Infrastructure.Clients.DocumentIntelligence;

namespace RagApp.UnitTests.Infrastructure.DocumentIntelligence;

[TestClass]
public class DocumentAnalysisClientTests
{
    private static (DocumentAnalysisClient Client, Mock<DocumentIntelligenceClient> Inner) BuildClient()
    {
        var inner  = new Mock<DocumentIntelligenceClient>();
        var client = new DocumentAnalysisClient(inner.Object);
        return (client, inner);
    }

    [TestMethod]
    public async Task SubmitAnalyzeAsync_StartsOperation_WithoutWaitingForCompletion()
    {
        var (client, inner) = BuildClient();
        var operation = new Mock<Operation<AnalyzeResult>>().Object;
        inner.Setup(c => c.AnalyzeDocumentAsync(WaitUntil.Started, It.IsAny<AnalyzeDocumentOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(operation);

        var options = new AnalyzeDocumentOptions("prebuilt-layout", new Uri("https://example.com/doc.pdf"));
        var result = await client.SubmitAnalyzeAsync(options);

        Assert.AreSame(operation, result);
        inner.Verify(c => c.AnalyzeDocumentAsync(WaitUntil.Started, options, It.IsAny<CancellationToken>()), Times.Once);
        inner.Verify(c => c.AnalyzeDocumentAsync(WaitUntil.Completed, It.IsAny<AnalyzeDocumentOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task SubmitAnalyzeAsync_ForwardsCancellationToken()
    {
        var (client, inner) = BuildClient();
        inner.Setup(c => c.AnalyzeDocumentAsync(It.IsAny<WaitUntil>(), It.IsAny<AnalyzeDocumentOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Mock<Operation<AnalyzeResult>>().Object);
        using var cts = new CancellationTokenSource();

        await client.SubmitAnalyzeAsync(new AnalyzeDocumentOptions("prebuilt-layout", new Uri("https://example.com/doc.pdf")), cts.Token);

        inner.Verify(c => c.AnalyzeDocumentAsync(It.IsAny<WaitUntil>(), It.IsAny<AnalyzeDocumentOptions>(), cts.Token), Times.Once);
    }
}
