using Azure;
using Azure.AI.ContentUnderstanding;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Infrastructure.Clients.ContentUnderstanding;
using AgenticRagApp.Infrastructure.Configuration;

namespace RagApp.UnitTests.Infrastructure;

// cap-pdf-layout is owned by Terraform (terraform_data.cu_analyzer,
// infra/content_understanding.tf), and that file's ONE WRITER PER OBJECT note requires any admin
// path in src/ to be get-and-verify only. These tests pin both halves of that: the drift detection
// is real, and nothing here ever writes.
//
// The drift checks matter because every one of the flags below fails *silently* at analyze time -
// the call succeeds, bills for every page, and returns the relevant collection empty. Catching that
// at analyze time means noticing a thin document; catching it here means reading an error.
[TestClass]
public class ContentUnderstandingVerifierTests
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

    // The definition Terraform actually applies. Anything that differs from this is drift.
    //
    // Built through ContentUnderstandingModelFactory rather than an object initializer because
    // Status is read-only on the model - and it matters that these fixtures carry a real Ready
    // status, since default(ContentAnalyzerStatus) is not Ready and would make every fixture fail
    // the status check for the wrong reason.
    private static ContentAnalyzer Analyzer(
        ContentAnalyzerStatus? status = null,
        string baseAnalyzerId = "prebuilt-document",
        bool enableOcr = true,
        bool enableLayout = true,
        bool returnDetails = true,
        bool figureDescription = true,
        bool figureAnalysis = true,
        TableFormat? tableFormat = null) =>
        ContentUnderstandingModelFactory.ContentAnalyzer(
            analyzerId:     "cap-pdf-layout",
            status:         status ?? ContentAnalyzerStatus.Ready,
            baseAnalyzerId: baseAnalyzerId,
            config: ContentUnderstandingModelFactory.ContentAnalyzerConfig(
                enableOcr:               enableOcr,
                enableLayout:            enableLayout,
                shouldReturnDetails:     returnDetails,
                enableFigureDescription: figureDescription,
                enableFigureAnalysis:    figureAnalysis,
                // Markdown, not Html. The pipeline asked for Html while PdfCleaner converted it
                // to pipe tables itself; with the cleaner gone nothing converts, and HTML markup
                // would reach the index verbatim.
                tableFormat:             tableFormat ?? TableFormat.Markdown),
            models: new Dictionary<string, string> { ["completion"] = "gpt-41-extraction" });

    private static ContentAnalyzer HealthyAnalyzer() => Analyzer();

    private static ContentUnderstandingVerifier BuildVerifier(
        Mock<ContentUnderstandingClient> client) =>
        new(client.Object, Config(), NullLogger<ContentUnderstandingVerifier>.Instance);

    private static Mock<ContentUnderstandingClient> ClientReturning(ContentAnalyzer analyzer)
    {
        var client = new Mock<ContentUnderstandingClient>();
        client.Setup(c => c.GetAnalyzerAsync("cap-pdf-layout", It.IsAny<CancellationToken>()))
              .ReturnsAsync(Response.FromValue(analyzer, Mock.Of<Response>()));
        return client;
    }

    [TestMethod]
    public async Task HealthyAnalyzerVerifiesClean()
    {
        var result = await BuildVerifier(ClientReturning(HealthyAnalyzer())).VerifyAnalyzerAsync();

        Assert.IsTrue(result.Ok, $"Expected a clean verification, got: {string.Join(" | ", result.Problems)}");
        Assert.IsTrue(result.Exists);
        Assert.AreEqual("prebuilt-document", result.BaseAnalyzerId);
    }

    [TestMethod]
    public async Task MissingAnalyzerIsReportedNotCreated()
    {
        var client = new Mock<ContentUnderstandingClient>();
        client.Setup(c => c.GetAnalyzerAsync("cap-pdf-layout", It.IsAny<CancellationToken>()))
              .ThrowsAsync(new RequestFailedException(404, "Not found"));

        var result = await BuildVerifier(client).VerifyAnalyzerAsync();

        Assert.IsFalse(result.Exists);
        Assert.IsFalse(result.Ok);

        // The point of the whole type: a missing analyzer must NOT be repaired from here.
        // Terraform owns the object; creating it would make src/ a second author.
        client.Verify(c => c.CreateAnalyzerAsync(
            It.IsAny<WaitUntil>(), It.IsAny<string>(), It.IsAny<ContentAnalyzer>(),
            It.IsAny<bool?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task FigureDescriptionOffIsCaught()
    {
        // The single flag the entire CU migration exists for: without it, 608 of the corpus's 617
        // figures stay dropped and the migration delivers nothing while costing full price.
        var analyzer = Analyzer(figureDescription: false);

        var result = await BuildVerifier(ClientReturning(analyzer)).VerifyAnalyzerAsync();

        Assert.IsFalse(result.Ok);
        Assert.IsTrue(result.Problems.Any(p => p.Contains("enableFigureDescription")),
            $"Expected an enableFigureDescription problem, got: {string.Join(" | ", result.Problems)}");
    }

    [TestMethod]
    public async Task HtmlTableFormatIsCaught()
    {
        // Html was the correct setting while PdfCleaner converted it to GFM pipe tables,
        // expanding merged cells into a full grid. Nothing converts now, so an analyzer still on
        // Html would put raw <table> markup into every chunk body and every embedding.
        var analyzer = Analyzer(tableFormat: TableFormat.Html);

        var result = await BuildVerifier(ClientReturning(analyzer)).VerifyAnalyzerAsync();

        Assert.IsFalse(result.Ok);
        Assert.IsTrue(result.Problems.Any(p => p.Contains("tableFormat")),
            $"Expected a tableFormat problem, got: {string.Join(" | ", result.Problems)}");
    }

    [TestMethod]
    public async Task ReturnDetailsOffIsCaught()
    {
        // The nastiest of the set: with returnDetails off, paragraphs/sections/tables/figures are
        // ALL empty no matter what the other flags say, so the analyzer looks correctly configured
        // while returning nothing any extraction helper can read.
        var analyzer = Analyzer(returnDetails: false);

        var result = await BuildVerifier(ClientReturning(analyzer)).VerifyAnalyzerAsync();

        Assert.IsFalse(result.Ok);
        Assert.IsTrue(result.Problems.Any(p => p.Contains("returnDetails")),
            $"Expected a returnDetails problem, got: {string.Join(" | ", result.Problems)}");
    }

    [TestMethod]
    public async Task WrongBaseAnalyzerIsCaught()
    {
        // prebuilt-documentSearch would bring service-side chunking that competes with the chunking
        // layer; prebuilt-layout cannot describe figures at all. See docs/2608/260821/cu-analyzer-choice.md.
        var analyzer = Analyzer(baseAnalyzerId: "prebuilt-documentSearch");

        var result = await BuildVerifier(ClientReturning(analyzer)).VerifyAnalyzerAsync();

        Assert.IsFalse(result.Ok);
        Assert.IsTrue(result.Problems.Any(p => p.Contains("BaseAnalyzerId")),
            $"Expected a BaseAnalyzerId problem, got: {string.Join(" | ", result.Problems)}");
    }

    [TestMethod]
    public async Task NotReadyStatusIsCaught()
    {
        var analyzer = Analyzer(status: ContentAnalyzerStatus.Creating);

        var result = await BuildVerifier(ClientReturning(analyzer)).VerifyAnalyzerAsync();

        Assert.IsFalse(result.Ok);
        Assert.IsTrue(result.Problems.Any(p => p.Contains("Status")),
            $"Expected a Status problem, got: {string.Join(" | ", result.Problems)}");
    }

    [TestMethod]
    public async Task VerifyNeverWritesDefaultsEither()
    {
        var client = ClientReturning(HealthyAnalyzer());

        await BuildVerifier(client).VerifyAnalyzerAsync();

        // Account defaults are shared, merge-patched, account-global state. Writing them is opt-in
        // at the endpoint (?setDefaults=true), never a side effect of a verification.
        client.Verify(c => c.UpdateDefaultsAsync(
            It.IsAny<IDictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
