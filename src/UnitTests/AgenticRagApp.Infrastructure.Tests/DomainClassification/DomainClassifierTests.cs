using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Infrastructure.Clients.DomainClassification;

namespace RagApp.UnitTests.Infrastructure;

[TestClass]
public class DomainClassifierTests
{
    private static DocumentToClassify Doc(string sourceId, string title = "Titel") =>
        new(sourceId, title, $"{title}\nInleiding");

    private static Mock<IChatClient> ChatReturning(params string[] responseTexts)
    {
        var chat = new Mock<IChatClient>();
        var sequence = chat.SetupSequence(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()));
        foreach (var text in responseTexts)
            sequence = sequence.ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        return chat;
    }

    private static DomainClassifier Build(Mock<IChatClient> chat) =>
        new(chat.Object, NullLogger<DomainClassifier>.Instance);

    [TestMethod]
    public async Task ClassifyAsync_ParsesTags_NormalizingCaseAndWhitespace()
    {
        var chat = ChatReturning("""
            {"documents":[
              {"id":"a.pdf","tag":" ggz "},
              {"id":"b.pdf","tag":null},
              {"id":"c.pdf","tag":"NONE"}
            ]}
            """);

        var result = await Build(chat).ClassifyAsync([Doc("a.pdf"), Doc("b.pdf"), Doc("c.pdf")]);

        Assert.AreEqual(3, result.Count);
        Assert.AreEqual("GGZ", result["a.pdf"]);
        // Both spellings of "no population applies" are the same classified answer: null.
        Assert.IsNull(result["b.pdf"]);
        Assert.IsNull(result["c.pdf"]);
    }

    [TestMethod]
    public async Task ClassifyAsync_MalformedTagsAndUnrequestedIds_AreDropped()
    {
        var chat = ChatReturning("""
            {"documents":[
              {"id":"a.pdf","tag":"not a tag"},
              {"id":"never-asked.pdf","tag":"GGZ"},
              {"id":"b.pdf","tag":"VVT"}
            ]}
            """);

        var result = await Build(chat).ClassifyAsync([Doc("a.pdf"), Doc("b.pdf")]);

        // a.pdf is ABSENT (failed, retried next run), not null (classified as no-population).
        Assert.IsFalse(result.ContainsKey("a.pdf"));
        Assert.IsFalse(result.ContainsKey("never-asked.pdf"));
        Assert.AreEqual("VVT", result["b.pdf"]);
    }

    [TestMethod]
    public async Task ClassifyAsync_FencedJson_StillParses()
    {
        var chat = ChatReturning("```json\n{\"documents\":[{\"id\":\"a.pdf\",\"tag\":\"LVB\"}]}\n```");

        var result = await Build(chat).ClassifyAsync([Doc("a.pdf")]);

        Assert.AreEqual("LVB", result["a.pdf"]);
    }

    [TestMethod]
    public async Task ClassifyAsync_ModelFailure_ReturnsEmptyInsteadOfThrowing()
    {
        var chat = new Mock<IChatClient>();
        chat.Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("deployment unreachable"));

        var result = await Build(chat).ClassifyAsync([Doc("a.pdf")]);

        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public async Task ClassifyAsync_NonJsonResponse_ReturnsEmptyInsteadOfThrowing()
    {
        var chat = ChatReturning("Sorry, I cannot help with that.");

        var result = await Build(chat).ClassifyAsync([Doc("a.pdf")]);

        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public async Task ClassifyAsync_EmptyInput_MakesNoModelCall()
    {
        var chat = ChatReturning();

        var result = await Build(chat).ClassifyAsync([]);

        Assert.AreEqual(0, result.Count);
        chat.Verify(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [TestMethod]
    public async Task ClassifyAsync_OverTheBatchSize_SplitsIntoMultipleCallsAndMergesResults()
    {
        // 26 documents at BatchSize 25: two calls, results merged. The responses only answer
        // for one document each - enough to prove both responses were consumed.
        var chat = ChatReturning(
            """{"documents":[{"id":"doc-0.pdf","tag":"GGZ"}]}""",
            """{"documents":[{"id":"doc-25.pdf","tag":"GHZ"}]}""");
        var docs = Enumerable.Range(0, 26).Select(i => Doc($"doc-{i}.pdf")).ToList();

        var result = await Build(chat).ClassifyAsync(docs);

        Assert.AreEqual("GGZ", result["doc-0.pdf"]);
        Assert.AreEqual("GHZ", result["doc-25.pdf"]);
        chat.Verify(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [TestMethod]
    public async Task ClassifyAsync_OneBatchFails_TheOtherBatchStillLands()
    {
        var chat = new Mock<IChatClient>();
        chat.SetupSequence(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("first batch dies"))
            .ReturnsAsync(new ChatResponse(new ChatMessage(
                ChatRole.Assistant, """{"documents":[{"id":"doc-25.pdf","tag":"MVB"}]}""")));
        var docs = Enumerable.Range(0, 26).Select(i => Doc($"doc-{i}.pdf")).ToList();

        var result = await Build(chat).ClassifyAsync(docs);

        Assert.IsFalse(result.ContainsKey("doc-0.pdf"));
        Assert.AreEqual("MVB", result["doc-25.pdf"]);
    }
}
