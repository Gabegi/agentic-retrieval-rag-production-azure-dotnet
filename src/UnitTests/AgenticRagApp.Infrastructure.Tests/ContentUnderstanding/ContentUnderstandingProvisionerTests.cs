using Azure;
using Azure.AI.ContentUnderstanding;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Infrastructure.Clients.ContentUnderstanding;
using AgenticRagApp.Infrastructure.Configuration;

namespace RagApp.UnitTests.Infrastructure;

// The writer half of CU bootstrap. Its counterpart, ContentUnderstandingVerifierTests, pins that
// the verifier never writes; these pin that the provisioner writes the right thing, in the right
// order, and only when it has to.
//
// The most load-bearing test here is DefinitionSatisfiesTheVerifiersAssertions: the definition and
// the assertions live in two files that must agree, and nothing but a test connects them. Without
// it, provisioning can cheerfully create an analyzer that its own read-back step then rejects.
[TestClass]
public class ContentUnderstandingProvisionerTests
{
    private static IndexerConfig Config() => new()
    {
        SearchEndpoint = "https://s.example.com", OpenAiEndpoint = "https://o.example.com",
        OpenAiEmbeddingDeployment = "embed", OpenAiGptDeployment = "gpt", OpenAiGptModelName = "gpt-5.4",
        ContentSafetyEndpoint = "https://cs.example.com", LanguageEndpoint = "https://l.example.com",
        StorageAccountUrl = "https://st.example.com", SearchIndexName = "idx",
        KnowledgeSourceName = "ks", KnowledgeBaseName = "kb",
        ContentUnderstandingAnalyzerId = "cap-pdf-layout",
    };

    // The cast is load-bearing: without it the ternary types as ContentAnalyzerStatus (a struct
    // with an implicit conversion from string), and the null branch goes through that conversion
    // and throws rather than producing a null status.
    private static AnalyzerVerification Verification(bool exists, params string[] problems) =>
        new("cap-pdf-layout", exists,
            exists ? ContentAnalyzerStatus.Ready : (ContentAnalyzerStatus?)null,
            exists ? "prebuilt-document" : null,
            new Dictionary<string, string>(), problems);

    private static Mock<IContentUnderstandingVerifier> Verifier(AnalyzerVerification verification)
    {
        var verifier = new Mock<IContentUnderstandingVerifier>();
        verifier.Setup(v => v.VerifyAnalyzerAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(verification);
        verifier.Setup(v => v.EnsureDefaultsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Dictionary<string, string> { ["gpt-5.4"] = "gpt-41-extraction" });
        return verifier;
    }

    private static ContentUnderstandingProvisioner Build(
        Mock<ContentUnderstandingClient> client, Mock<IContentUnderstandingVerifier> verifier) =>
        new(client.Object, verifier.Object, Config(),
            NullLogger<ContentUnderstandingProvisioner>.Instance);

    private static Mock<ContentUnderstandingClient> Client()
    {
        var client = new Mock<ContentUnderstandingClient>();
        client.Setup(c => c.CreateAnalyzerAsync(
                  It.IsAny<WaitUntil>(), It.IsAny<string>(), It.IsAny<ContentAnalyzer>(),
                  It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Mock.Of<Operation<ContentAnalyzer>>());
        return client;
    }

    // --- The definition itself --------------------------------------------------------

    [TestMethod]
    public void DefinitionSatisfiesTheVerifiersAssertions()
    {
        // Round-trip: take what the provisioner would PUT, present it back as the analyzer the
        // service would then return, and run the real verifier over it. Status has to be supplied
        // separately because it is read-only on the model and absent from a request body -
        // everything else is exactly what BuildAnalyzer produced.
        var definition = new ContentUnderstandingProvisioner(
            Mock.Of<ContentUnderstandingClient>(), Mock.Of<IContentUnderstandingVerifier>(),
            Config(), NullLogger<ContentUnderstandingProvisioner>.Instance).BuildAnalyzer();

        var asReturnedByService = ContentUnderstandingModelFactory.ContentAnalyzer(
            analyzerId:     "cap-pdf-layout",
            status:         ContentAnalyzerStatus.Ready,
            baseAnalyzerId: definition.BaseAnalyzerId,
            config: ContentUnderstandingModelFactory.ContentAnalyzerConfig(
                enableOcr:               definition.Config!.EnableOcr,
                enableLayout:            definition.Config.EnableLayout,
                shouldReturnDetails:     definition.Config.ShouldReturnDetails,
                enableFigureDescription: definition.Config.EnableFigureDescription,
                enableFigureAnalysis:    definition.Config.EnableFigureAnalysis,
                tableFormat:             definition.Config.TableFormat,
                annotationFormat:        definition.Config.AnnotationFormat),
            models: definition.Models);

        var client = new Mock<ContentUnderstandingClient>();
        client.Setup(c => c.GetAnalyzerAsync("cap-pdf-layout", It.IsAny<CancellationToken>()))
              .ReturnsAsync(Response.FromValue(asReturnedByService, Mock.Of<Response>()));

        var verification = new ContentUnderstandingVerifier(
            client.Object, Config(), NullLogger<ContentUnderstandingVerifier>.Instance)
            .VerifyAnalyzerAsync().GetAwaiter().GetResult();

        Assert.IsTrue(verification.Ok,
            "The provisioned definition must satisfy every assertion the verifier makes. Drift: " +
            string.Join(" | ", verification.Problems));
    }

    [TestMethod]
    public void DefinitionPinsTheModelNameNotTheDeploymentName()
    {
        // The trap: models maps ROLE -> MODEL, while the account defaults map MODEL -> DEPLOYMENT.
        // A deployment name here is accepted at provisioning time and fails at analyze time.
        var definition = new ContentUnderstandingProvisioner(
            Mock.Of<ContentUnderstandingClient>(), Mock.Of<IContentUnderstandingVerifier>(),
            Config(), NullLogger<ContentUnderstandingProvisioner>.Instance).BuildAnalyzer();

        Assert.AreEqual(Config().ContentUnderstandingCompletionModel, definition.Models["completion"]);
        Assert.AreNotEqual(Config().OpenAiExtractionDeployment, definition.Models["completion"]);
    }

    // --- Idempotence and drift --------------------------------------------------------

    [TestMethod]
    public async Task AnAnalyzerThatVerifiesCleanIsLeftAlone()
    {
        // The expected outcome of every deploy after the first, and the whole reason this is safe
        // to run unconditionally at the end of a pipeline stage.
        var client   = Client();
        var verifier = Verifier(Verification(exists: true));

        var result = await Build(client, verifier).ProvisionAsync();

        Assert.AreEqual(AnalyzerProvisioningAction.LeftAsIs, result.AnalyzerAction);
        Assert.IsTrue(result.Ok);
        client.Verify(c => c.CreateAnalyzerAsync(
            It.IsAny<WaitUntil>(), It.IsAny<string>(), It.IsAny<ContentAnalyzer>(),
            It.IsAny<bool?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task AMissingAnalyzerIsCreated()
    {
        var client   = Client();
        var verifier = new Mock<IContentUnderstandingVerifier>();
        verifier.SetupSequence(v => v.VerifyAnalyzerAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(Verification(exists: false, "Analyzer does not exist."))
                .ReturnsAsync(Verification(exists: true));
        verifier.Setup(v => v.EnsureDefaultsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Dictionary<string, string>());

        var result = await Build(client, verifier).ProvisionAsync();

        Assert.AreEqual(AnalyzerProvisioningAction.Created, result.AnalyzerAction);

        // allowReplace stays false when nothing is there to replace, so a race that created the
        // analyzer between the verify and the PUT surfaces as a conflict rather than silently
        // overwriting whatever the other writer put there.
        client.Verify(c => c.CreateAnalyzerAsync(
            WaitUntil.Completed, "cap-pdf-layout", It.IsAny<ContentAnalyzer>(),
            false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task ADriftedAnalyzerIsReplacedWithoutBeingAsked()
    {
        // The alternative is a deploy step that reports success against an analyzer known to be
        // wrong. Replacing is cheap here in a way it would not be for a stateful resource: an
        // analyzer holds no data, so re-creating it costs one LRO rather than a re-analysis.
        var client   = Client();
        var verifier = new Mock<IContentUnderstandingVerifier>();
        verifier.SetupSequence(v => v.VerifyAnalyzerAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(Verification(exists: true, "tableFormat is 'Html', expected Markdown"))
                .ReturnsAsync(Verification(exists: true));
        verifier.Setup(v => v.EnsureDefaultsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Dictionary<string, string>());

        var result = await Build(client, verifier).ProvisionAsync();

        Assert.AreEqual(AnalyzerProvisioningAction.Replaced, result.AnalyzerAction);
        client.Verify(c => c.CreateAnalyzerAsync(
            WaitUntil.Completed, "cap-pdf-layout", It.IsAny<ContentAnalyzer>(),
            true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task ReplaceTrueRewritesAnAnalyzerThatWasAlreadyClean()
    {
        var client   = Client();
        var verifier = Verifier(Verification(exists: true));

        var result = await Build(client, verifier).ProvisionAsync(replaceAnalyzer: true);

        Assert.AreEqual(AnalyzerProvisioningAction.Replaced, result.AnalyzerAction);
        client.Verify(c => c.CreateAnalyzerAsync(
            WaitUntil.Completed, "cap-pdf-layout", It.IsAny<ContentAnalyzer>(),
            true, It.IsAny<CancellationToken>()), Times.Once);
    }

    // --- Ordering and async -----------------------------------------------------------

    [TestMethod]
    public async Task DefaultsAreWrittenBeforeTheAnalyzer()
    {
        // Analyzer creation validates the deployments its Models block references against the
        // account defaults. Analyzer-first fails as "Model deployment not found", which reads
        // like a missing OpenAI deployment and sends whoever is debugging it to the wrong file.
        var calls = new List<string>();

        var client = new Mock<ContentUnderstandingClient>();
        client.Setup(c => c.CreateAnalyzerAsync(
                  It.IsAny<WaitUntil>(), It.IsAny<string>(), It.IsAny<ContentAnalyzer>(),
                  It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
              .Callback(() => calls.Add("analyzer"))
              .ReturnsAsync(Mock.Of<Operation<ContentAnalyzer>>());

        var verifier = new Mock<IContentUnderstandingVerifier>();
        verifier.Setup(v => v.EnsureDefaultsAsync(It.IsAny<CancellationToken>()))
                .Callback(() => calls.Add("defaults"))
                .ReturnsAsync(new Dictionary<string, string>());
        verifier.SetupSequence(v => v.VerifyAnalyzerAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(Verification(exists: false, "Analyzer does not exist."))
                .ReturnsAsync(Verification(exists: true));

        await Build(client, verifier).ProvisionAsync();

        CollectionAssert.AreEqual(new[] { "defaults", "analyzer" }, calls);
    }

    [TestMethod]
    public async Task TheCreateWaitsForTheOperationToComplete()
    {
        // A fire-and-forget PUT reports success for an analyzer still in Creating, and the first
        // analyze call then races it.
        var client   = Client();
        var verifier = new Mock<IContentUnderstandingVerifier>();
        verifier.SetupSequence(v => v.VerifyAnalyzerAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(Verification(exists: false, "Analyzer does not exist."))
                .ReturnsAsync(Verification(exists: true));
        verifier.Setup(v => v.EnsureDefaultsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Dictionary<string, string>());

        await Build(client, verifier).ProvisionAsync();

        client.Verify(c => c.CreateAnalyzerAsync(
            WaitUntil.Completed, It.IsAny<string>(), It.IsAny<ContentAnalyzer>(),
            It.IsAny<bool?>(), It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(c => c.CreateAnalyzerAsync(
            WaitUntil.Started, It.IsAny<string>(), It.IsAny<ContentAnalyzer>(),
            It.IsAny<bool?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // --- Read-back --------------------------------------------------------------------

    [TestMethod]
    public async Task AnAnalyzerThatStillFailsVerificationAfterProvisioningIsNotOk()
    {
        // The read-back is the point: a PUT returning 200 says the service accepted the body, not
        // that what it built satisfies the pipeline's assertions.
        var client   = Client();
        var verifier = new Mock<IContentUnderstandingVerifier>();
        verifier.SetupSequence(v => v.VerifyAnalyzerAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(Verification(exists: false, "Analyzer does not exist."))
                .ReturnsAsync(Verification(exists: true, "enableFigureDescription is off"));
        verifier.Setup(v => v.EnsureDefaultsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Dictionary<string, string>());

        var result = await Build(client, verifier).ProvisionAsync();

        Assert.AreEqual(AnalyzerProvisioningAction.Created, result.AnalyzerAction);
        Assert.IsFalse(result.Ok);
        CollectionAssert.Contains(result.Verification.Problems.ToList(), "enableFigureDescription is off");
    }
}
