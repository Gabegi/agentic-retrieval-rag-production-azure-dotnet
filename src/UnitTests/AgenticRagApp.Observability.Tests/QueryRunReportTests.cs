using AgenticRagApp.Observability.Reports;

namespace RagApp.UnitTests.Observability;

[TestClass]
public class QueryRunReportTests
{
    private static QueryRunReport Build(
        long? seed = 42,
        float? temperature = 0.2f,
        int? maxOutputTokens = 512,
        float? topP = 0.9f,
        int? topK = 40,
        float? frequencyPenalty = 0f,
        float? presencePenalty = 0f,
        string? responseFormat = "text",
        IReadOnlyList<string>? stopSequences = null) => new(
        RunId:              "run-1",
        Timestamp:          DateTimeOffset.Parse("2026-07-24T10:00:00Z"),
        Question:           "What is the policy?",
        Answer:             "The policy is X.",
        RetrievedContext:   "context chunk",
        SystemInstructions: "answer using retrieved context",
        ChunksRetrieved:    3,
        OperationName:      "chat.completions",
        ProviderName:       "azure.ai.openai",
        ServerAddress:      "myresource.openai.azure.com",
        ServerPort:         443,
        ConversationId:     "conv-1",
        Model:              "gpt-4.1",
        FinishReason:       "stop",
        LatencyMs:          1234,
        InputTokens:        100,
        OutputTokens:       50,
        TotalTokens:        150,
        Temperature:        temperature,
        MaxOutputTokens:    maxOutputTokens,
        TopP:               topP,
        TopK:               topK,
        FrequencyPenalty:   frequencyPenalty,
        PresencePenalty:    presencePenalty,
        Seed:               seed,
        ResponseFormat:     responseFormat,
        StopSequences:      stopSequences);

    [TestMethod]
    public void Constructor_PropagatesAllRequiredFields()
    {
        var report = Build();

        Assert.AreEqual("run-1", report.RunId);
        Assert.AreEqual("What is the policy?", report.Question);
        Assert.AreEqual("The policy is X.", report.Answer);
        Assert.AreEqual("context chunk", report.RetrievedContext);
        Assert.AreEqual("answer using retrieved context", report.SystemInstructions);
        Assert.AreEqual(3, report.ChunksRetrieved);
        Assert.AreEqual("chat.completions", report.OperationName);
        Assert.AreEqual("azure.ai.openai", report.ProviderName);
        Assert.AreEqual("myresource.openai.azure.com", report.ServerAddress);
        Assert.AreEqual(443, report.ServerPort);
        Assert.AreEqual("conv-1", report.ConversationId);
        Assert.AreEqual("gpt-4.1", report.Model);
        Assert.AreEqual("stop", report.FinishReason);
        Assert.AreEqual(1234L, report.LatencyMs);
        Assert.AreEqual(100L, report.InputTokens);
        Assert.AreEqual(50L, report.OutputTokens);
        Assert.AreEqual(150L, report.TotalTokens);
    }

    [TestMethod]
    public void Constructor_AllowsAllOptionalSamplingParametersToBeNull()
    {
        var report = Build(
            seed: null, temperature: null, maxOutputTokens: null, topP: null, topK: null,
            frequencyPenalty: null, presencePenalty: null, responseFormat: null, stopSequences: null);

        Assert.IsNull(report.Seed);
        Assert.IsNull(report.Temperature);
        Assert.IsNull(report.MaxOutputTokens);
        Assert.IsNull(report.TopP);
        Assert.IsNull(report.TopK);
        Assert.IsNull(report.FrequencyPenalty);
        Assert.IsNull(report.PresencePenalty);
        Assert.IsNull(report.ResponseFormat);
        Assert.IsNull(report.StopSequences);
    }

    [TestMethod]
    public void Constructor_PropagatesOptionalSamplingParametersWhenProvided()
    {
        var report = Build(stopSequences: ["\n\n", "END"]);

        Assert.AreEqual(42L, report.Seed);
        Assert.AreEqual(0.2f, report.Temperature);
        Assert.AreEqual(512, report.MaxOutputTokens);
        Assert.AreEqual(0.9f, report.TopP);
        Assert.AreEqual(40, report.TopK);
        Assert.AreEqual("text", report.ResponseFormat);
        CollectionAssert.AreEqual(new[] { "\n\n", "END" }, report.StopSequences!.ToList());
    }

    [TestMethod]
    public void RecordEquality_SameValues_AreEqual()
    {
        Assert.AreEqual(Build(), Build());
    }
}
