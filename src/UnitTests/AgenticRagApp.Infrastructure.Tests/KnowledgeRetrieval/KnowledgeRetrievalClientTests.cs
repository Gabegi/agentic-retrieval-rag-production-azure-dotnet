using System.ClientModel.Primitives;
using System.Text.Json;
using Azure;
using Azure.Search.Documents.KnowledgeBases;
using Azure.Search.Documents.KnowledgeBases.Models;
using Moq;
using AgenticRagApp.Infrastructure.Clients.KnowledgeRetrieval;

namespace RagApp.UnitTests.Infrastructure.KnowledgeRetrieval;

[TestClass]
public class KnowledgeRetrievalClientTests
{
    // KnowledgeBaseRetrievalResponse is an Azure SDK response-only model (no public
    // constructor) - built via ModelReaderWriter from JSON, same approach as
    // AgenticRagQueryServiceTests.RetrievalResponse.
    private static KnowledgeBaseRetrievalResponse EmptyResponse()
    {
        var payload = new Dictionary<string, object?>
        {
            ["references"] = Array.Empty<object>(),
            ["response"]   = Array.Empty<object>(),
            ["activity"]   = Array.Empty<object>(),
        };
        var json = JsonSerializer.Serialize(payload);
        return ModelReaderWriter.Read<KnowledgeBaseRetrievalResponse>(BinaryData.FromString(json))!;
    }

    private static KnowledgeBaseRetrievalRequest Request() => new()
    {
        Messages =
        {
            new KnowledgeBaseMessage(new KnowledgeBaseMessageContent[] { new KnowledgeBaseMessageTextContent("question") })
            { Role = "user" },
        },
    };

    [TestMethod]
    public async Task RetrieveAsync_ReturnsUnderlyingClientsResponseValue()
    {
        var response   = EmptyResponse();
        var underlying = new Mock<KnowledgeBaseRetrievalClient>();
        underlying
            .Setup(c => c.RetrieveAsync(It.IsAny<KnowledgeBaseRetrievalRequest>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(response, Mock.Of<Response>()));
        var client = new KnowledgeBaseClient(underlying.Object);

        var result = await client.RetrieveAsync(Request());

        Assert.AreSame(response, result);
    }

    [TestMethod]
    public async Task RetrieveAsync_PassesRequestThroughToUnderlyingClient()
    {
        var request    = Request();
        var underlying = new Mock<KnowledgeBaseRetrievalClient>();
        underlying
            .Setup(c => c.RetrieveAsync(request, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(EmptyResponse(), Mock.Of<Response>()));
        var client = new KnowledgeBaseClient(underlying.Object);

        await client.RetrieveAsync(request);

        underlying.Verify(c => c.RetrieveAsync(request, null, It.IsAny<CancellationToken>()), Times.Once);
    }
}
