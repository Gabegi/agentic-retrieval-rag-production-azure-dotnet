using Azure;
using Azure.AI.TextAnalytics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Querying.Guards;

namespace RagApp.UnitTests.Querying;

// Two layers, tested separately. The local layer (BSN elfproef, Dutch postcode + house
// number) runs against the full text and never calls out, so it is exercised with a client
// that would throw if touched. The Azure layer is exercised through a mocked
// TextAnalyticsClient, where the interesting cases are the two that must fail CLOSED - a
// per-document error and a service failure - because the safe default for a compliance guard
// is "block", and a bug that flipped either of them to false would be invisible in
// production.
[TestClass]
public class PiiGuardTests
{
    // Passes the elfproef: 9*1+8*2+7*3+6*4+5*5+4*6+3*7+2*8 = 156, minus the check digit 2 is
    // 154, which is 14*11. Not a real person's number - it is the canonical example value.
    private const string ValidBsn = "123456782";

    // Same digits with the check digit left at 9: 156-9 = 147, not divisible by 11.
    private const string InvalidBsn = "123456789";

    private static Mock<TextAnalyticsClient> ClientReturning(params RecognizePiiEntitiesResult[] results)
    {
        var client = new Mock<TextAnalyticsClient>();
        client.Setup(c => c.RecognizePiiEntitiesBatchAsync(
                It.IsAny<IEnumerable<TextDocumentInput>>(), It.IsAny<RecognizePiiEntitiesOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(
                TextAnalyticsModelFactory.RecognizePiiEntitiesResultCollection(results, default, "latest"),
                Mock.Of<Response>()));
        return client;
    }

    private static RecognizePiiEntitiesResult Clean(string id = "0") =>
        TextAnalyticsModelFactory.RecognizePiiEntitiesResult(
            id, default, TextAnalyticsModelFactory.PiiEntityCollection([], "", []));

    private static RecognizePiiEntitiesResult Detected(PiiEntityCategory category, double confidence, string id = "0") =>
        TextAnalyticsModelFactory.RecognizePiiEntitiesResult(
            id, default,
            TextAnalyticsModelFactory.PiiEntityCollection(
                [TextAnalyticsModelFactory.PiiEntity("Jan Jansen", category.ToString(), null, confidence, 0, 10)],
                "*** ******", []));

    private static PiiGuard Guard(Mock<TextAnalyticsClient> client) =>
        new(client.Object, NullLogger<PiiGuard>.Instance);

    // A client that fails the test if the guard reaches Azure at all - the local checks are
    // supposed to short-circuit before any paid call.
    private static Mock<TextAnalyticsClient> NeverCalled()
    {
        var client = new Mock<TextAnalyticsClient>(MockBehavior.Strict);
        return client;
    }

    // ── local checks ─────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ContainsPiiAsync_EmptyOrWhitespace_IsNotPiiAndDoesNotCallOut()
    {
        var guard = Guard(NeverCalled());

        Assert.IsFalse(await guard.ContainsPiiAsync(""));
        Assert.IsFalse(await guard.ContainsPiiAsync("   \n\t "));
    }

    [TestMethod]
    public async Task ContainsPiiAsync_ValidBsn_IsBlockedLocally()
    {
        var guard = Guard(NeverCalled());

        Assert.IsTrue(await guard.ContainsPiiAsync($"mijn bsn is {ValidBsn}"));
    }

    [TestMethod]
    public async Task ContainsPiiAsync_ValidBsnWithSeparators_IsStillBlocked()
    {
        // The number is written a handful of ways in practice; matching only the bare digits
        // would miss the form people actually type.
        var guard = Guard(NeverCalled());

        Assert.IsTrue(await guard.ContainsPiiAsync("bsn 123 456 782"));
    }

    [TestMethod]
    public async Task ContainsPiiAsync_NineDigitsThatFailTheElfproef_IsNotBlockedLocally()
    {
        // A nine-digit number that is not a BSN (an order or invoice number) must fall
        // through to the Azure layer rather than being blocked outright.
        var client = ClientReturning(Clean());

        Assert.IsFalse(await Guard(client).ContainsPiiAsync($"factuurnummer {InvalidBsn}"));
    }

    [TestMethod]
    public async Task ContainsPiiAsync_DutchPostcodeWithHouseNumber_IsBlockedLocally()
    {
        var guard = Guard(NeverCalled());

        Assert.IsTrue(await guard.ContainsPiiAsync("hij woont op 1012 AB 25"));
    }

    [TestMethod]
    public async Task ContainsPiiAsync_PostcodeWithoutHouseNumber_IsNotBlockedLocally()
    {
        // A bare postcode identifies a street, not a person - the house number is what makes
        // it an address, and blocking on the postcode alone would reject ordinary questions.
        var client = ClientReturning(Clean());

        Assert.IsFalse(await Guard(client).ContainsPiiAsync("het kantoor zit in 1012 AB"));
    }

    // ── Azure AI Language layer ──────────────────────────────────────────────

    [TestMethod]
    public async Task ContainsPiiAsync_BlockedCategoryAboveThreshold_IsPii()
    {
        var client = ClientReturning(Detected(PiiEntityCategory.Person, 0.95));

        Assert.IsTrue(await Guard(client).ContainsPiiAsync("wie is de contactpersoon?"));
    }

    [TestMethod]
    public async Task ContainsPiiAsync_BlockedCategoryBelowThreshold_IsNotPii()
    {
        // Below 0.75 the detector is guessing; blocking on it would reject ordinary Dutch
        // words that happen to look like names.
        var client = ClientReturning(Detected(PiiEntityCategory.Person, 0.5));

        Assert.IsFalse(await Guard(client).ContainsPiiAsync("wie is de contactpersoon?"));
    }

    [TestMethod]
    public async Task ContainsPiiAsync_UnblockedCategory_IsNotPii()
    {
        // The category list is deliberately narrower than everything the detector can find -
        // an organization name in a policy question is not PII.
        var client = ClientReturning(Detected(PiiEntityCategory.Organization, 0.99));

        Assert.IsFalse(await Guard(client).ContainsPiiAsync("wat is het beleid?"));
    }

    [TestMethod]
    public async Task ContainsPiiAsync_PerDocumentError_FailsClosed()
    {
        // A document the service could not analyze is not a document that is clean.
        var errored = TextAnalyticsModelFactory.RecognizePiiEntitiesResult(
            "0", TextAnalyticsModelFactory.TextAnalyticsError("InvalidDocument", "unsupported language"));
        var client = ClientReturning(errored);

        Assert.IsTrue(await Guard(client).ContainsPiiAsync("een vraag"));
    }

    [TestMethod]
    public async Task ContainsPiiAsync_ServiceFailure_FailsClosed()
    {
        var client = new Mock<TextAnalyticsClient>();
        client.Setup(c => c.RecognizePiiEntitiesBatchAsync(
                It.IsAny<IEnumerable<TextDocumentInput>>(), It.IsAny<RecognizePiiEntitiesOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(503, "Service Unavailable"));

        Assert.IsTrue(await Guard(client).ContainsPiiAsync("een vraag"));
    }

    [TestMethod]
    public async Task ContainsPiiAsync_SendsTheTextAsDutch()
    {
        // The detector's Dutch model is what the corpus and the questions are in; letting it
        // language-detect per call makes the guard's behaviour vary with the input.
        IEnumerable<TextDocumentInput>? sent = null;
        var client = ClientReturning(Clean());
        client.Setup(c => c.RecognizePiiEntitiesBatchAsync(
                It.IsAny<IEnumerable<TextDocumentInput>>(), It.IsAny<RecognizePiiEntitiesOptions>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<TextDocumentInput>, RecognizePiiEntitiesOptions, CancellationToken>((d, _, _) => sent = d.ToList())
            .ReturnsAsync(Response.FromValue(
                TextAnalyticsModelFactory.RecognizePiiEntitiesResultCollection([Clean()], default, "latest"),
                Mock.Of<Response>()));

        await Guard(client).ContainsPiiAsync("een gewone vraag");

        Assert.IsNotNull(sent);
        Assert.AreEqual("nl", sent!.Single().Language);
    }

    // ── chunking ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ContainsPiiAsync_ShortText_IsSentAsASingleDocument()
    {
        var documentCounts = new List<int>();
        var client = ClientReturning(Clean());
        CaptureDocumentCounts(client, documentCounts);

        await Guard(client).ContainsPiiAsync("een korte vraag");

        CollectionAssert.AreEqual(new[] { 1 }, documentCounts);
    }

    [TestMethod]
    public async Task ContainsPiiAsync_TextOverTheDocumentLimit_IsSplitIntoChunksWithinTheLimit()
    {
        // Azure AI Language caps a document at 5,120 characters. A text over that has to be
        // split, and every chunk has to come back under the cap or the call is rejected.
        var chunkLengths = new List<int>();
        var client = ClientReturning(Clean());
        client.Setup(c => c.RecognizePiiEntitiesBatchAsync(
                It.IsAny<IEnumerable<TextDocumentInput>>(), It.IsAny<RecognizePiiEntitiesOptions>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<TextDocumentInput>, RecognizePiiEntitiesOptions, CancellationToken>(
                (d, _, _) => chunkLengths.AddRange(d.Select(x => x.Text.Length)))
            .ReturnsAsync(Response.FromValue(
                TextAnalyticsModelFactory.RecognizePiiEntitiesResultCollection([Clean()], default, "latest"),
                Mock.Of<Response>()));

        var sentence = "Dit is een gewone Nederlandse zin zonder persoonsgegevens erin. ";
        await Guard(client).ContainsPiiAsync(string.Concat(Enumerable.Repeat(sentence, 200)));

        Assert.IsTrue(chunkLengths.Count > 1, "A text well over the per-document cap should have been split.");
        Assert.IsTrue(chunkLengths.All(l => l <= 5_000), "Every chunk must stay within the per-document cap.");
    }

    [TestMethod]
    public async Task ContainsPiiAsync_ManyChunks_AreBatchedFiveAtATime()
    {
        // Five documents per synchronous request is the other service limit; exceeding it
        // fails the whole call rather than truncating it.
        var documentCounts = new List<int>();
        var client = ClientReturning(Clean());
        CaptureDocumentCounts(client, documentCounts);

        var sentence = "Dit is een gewone Nederlandse zin zonder persoonsgegevens erin. ";
        await Guard(client).ContainsPiiAsync(string.Concat(Enumerable.Repeat(sentence, 800)));

        Assert.IsTrue(documentCounts.Count > 1, "50,000 characters should not fit in one request.");
        Assert.IsTrue(documentCounts.All(c => c <= 5), "No request may carry more than five documents.");
    }

    [TestMethod]
    public async Task ContainsPiiAsync_DetectionInALaterChunk_IsStillFound()
    {
        // The result loop has to inspect every document in the batch, not just the first.
        var client = new Mock<TextAnalyticsClient>();
        client.Setup(c => c.RecognizePiiEntitiesBatchAsync(
                It.IsAny<IEnumerable<TextDocumentInput>>(), It.IsAny<RecognizePiiEntitiesOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(
                TextAnalyticsModelFactory.RecognizePiiEntitiesResultCollection(
                    [Clean("0"), Detected(PiiEntityCategory.Address, 0.9, "1")], default, "latest"),
                Mock.Of<Response>()));

        Assert.IsTrue(await Guard(client).ContainsPiiAsync("een vraag"));
    }

    private static void CaptureDocumentCounts(Mock<TextAnalyticsClient> client, List<int> counts) =>
        client.Setup(c => c.RecognizePiiEntitiesBatchAsync(
                It.IsAny<IEnumerable<TextDocumentInput>>(), It.IsAny<RecognizePiiEntitiesOptions>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<TextDocumentInput>, RecognizePiiEntitiesOptions, CancellationToken>((d, _, _) => counts.Add(d.Count()))
            .ReturnsAsync(Response.FromValue(
                TextAnalyticsModelFactory.RecognizePiiEntitiesResultCollection([Clean()], default, "latest"),
                Mock.Of<Response>()));
}
