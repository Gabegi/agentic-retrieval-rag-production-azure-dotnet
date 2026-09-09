using Azure;
using Azure.AI.TextAnalytics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Infrastructure.Clients.Language;

namespace RagApp.UnitTests.Infrastructure.Language;

// The producer for the index's `language` field. The interesting cases are the three that must
// return NULL rather than a value: an undetermined response, a service failure, and an empty
// sample - because a facetable field is worse when it carries a fabricated code than when it
// carries nothing. Degrade-never-throw is the contract; an extraction run must not fail over a
// language guess.
[TestClass]
public class DocumentLanguageDetectorTests
{
    [TestMethod]
    public async Task DetectsTheIsoCodeAndCarriesTheConfidence()
    {
        var client = ClientReturning("Dutch", "nl", 0.98f);

        var detected = await Detector(client).DetectAsync("Dit is een Nederlands document.");

        Assert.IsNotNull(detected);
        // The two-letter code, not the spelled-out name: the index field is filtered on.
        Assert.AreEqual("nl", detected.Iso6391Name);
        Assert.AreEqual(0.98, detected.Confidence!.Value, 1e-6);
    }

    [TestMethod]
    public async Task TheEnglishDocumentIsDetectedAsEnglish_NotAsTheCorpusDefault()
    {
        // No countryHint is sent for exactly this reason - a "nl" hint would bias the one case
        // this field exists to identify (the ~4 chars/token document).
        var client = ClientReturning("English", "en", 0.95f);

        var detected = await Detector(client).DetectAsync("This is an English document.");

        Assert.AreEqual("en", detected!.Iso6391Name);
    }

    [TestMethod]
    public async Task AnUndeterminedResponseIsNull_NotAnEmptyLanguageCode()
    {
        // The service reports undetermined input as "(Unknown)" with no ISO name, and does so
        // as a SUCCESS - so this is a shape check, not an error path.
        var client = ClientReturning("(Unknown)", "", 0f);

        Assert.IsNull(await Detector(client).DetectAsync("?!?!"));
    }

    [TestMethod]
    public async Task AServiceFailureLeavesTheLanguageUnset_AndDoesNotThrow()
    {
        var client = new Mock<TextAnalyticsClient>();
        client.Setup(c => c.DetectLanguageAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(429, "Too many requests"));

        Assert.IsNull(await Detector(client).DetectAsync("Dit is een Nederlands document."));
    }

    [TestMethod]
    public async Task AnEmptySampleIsNotSentToTheService()
    {
        // Nothing to detect, so nothing is billed or waited on. Strict mock: any call fails.
        var client = new Mock<TextAnalyticsClient>(MockBehavior.Strict);

        Assert.IsNull(await Detector(client).DetectAsync(""));
        Assert.IsNull(await Detector(client).DetectAsync("   "));
    }

    [TestMethod]
    public async Task ASampleOverTheServiceLimitIsTruncatedRatherThanRejected()
    {
        // Azure AI Language accepts 5,120 chars per document. Callers send far less, but the
        // detector enforces the bound itself so a future caller cannot breach it.
        var seen = new List<int>();
        var client = new Mock<TextAnalyticsClient>();
        client.Setup(c => c.DetectLanguageAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string text, string _, CancellationToken _) => seen.Add(text.Length))
            .ReturnsAsync(Response.FromValue(
                TextAnalyticsModelFactory.DetectedLanguage("Dutch", "nl", 0.9f, []),
                Mock.Of<Response>()));

        await Detector(client).DetectAsync(new string('a', 9_000));

        Assert.AreEqual(1, seen.Count);
        Assert.IsTrue(seen[0] <= 5_000, $"sent {seen[0]} chars");
    }

    private static Mock<TextAnalyticsClient> ClientReturning(string name, string iso, float score)
    {
        var client = new Mock<TextAnalyticsClient>();
        client.Setup(c => c.DetectLanguageAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(
                TextAnalyticsModelFactory.DetectedLanguage(name, iso, score, []),
                Mock.Of<Response>()));
        return client;
    }

    private static DocumentLanguageDetector Detector(Mock<TextAnalyticsClient> client) =>
        new(client.Object, NullLogger<DocumentLanguageDetector>.Instance);
}
