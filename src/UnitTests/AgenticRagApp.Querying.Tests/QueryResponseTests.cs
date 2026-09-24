using System.Text.Json;
using AgenticRagApp.Querying.Models;
using AgenticRagApp.Querying.Services;

namespace RagApp.UnitTests.Querying;

// Pins the wire contract of POST /api/query. Two hosts serialize QueryResponse with two
// different default serializers (the Functions worker's plain System.Text.Json, ASP.NET Core's
// camelCase Web defaults), so the names are asserted under both: the [JsonPropertyName]
// attributes must win over either policy, or the hosts drift apart.
[TestClass]
public class QueryResponseTests
{
    private static RagQueryResult Result(IReadOnlyList<Citation> citations, string? category = null) => new(
        Answer:              "Het antwoord",
        RetrievedContext:    "context",
        SystemInstructions:  "instructions",
        ChunksRetrieved:     3,
        OperationName:       "chat",
        ProviderName:        "azure_openai",
        ServerAddress:       "openai.example.com",
        ServerPort:          443,
        ConversationId:      "conv-1",
        Model:               "gpt-model",
        FinishReason:        "stop",
        Category:            category,
        LatencyMs:           123,
        InputTokens:         10,
        OutputTokens:        20,
        TotalTokens:         30,
        ContextTokens:       15,
        Temperature:         null,
        MaxOutputTokens:     null,
        TopP:                null,
        TopK:                null,
        FrequencyPenalty:    null,
        PresencePenalty:     null,
        Seed:                null,
        ResponseFormat:      null,
        StopSequences:       null,
        Citations:           citations);

    private static readonly string[] ExpectedKeys =
    [
        "\"answer\"", "\"category\"", "\"sources\"", "\"telemetry\"",
        "\"document_id\"", "\"title\"", "\"quick_code\"", "\"relative_path\"", "\"page\"",
        "\"page_count\"", "\"created_at\"", "\"mod_date\"", "\"label\"", "\"url\"",
        "\"latency_ms\"", "\"input_tokens\"", "\"output_tokens\"",
    ];

    [TestMethod]
    public void From_SerializedWithDefaultOptions_UsesTheSnakeCaseNamesTheFunctionsHostAlwaysSent()
    {
        var response = QueryResponse.From(Result([new Citation("doc1", "Title", "QC1", "rel/path", Page: 2)]));

        var json = JsonSerializer.Serialize(response);

        foreach (var key in ExpectedKeys)
            StringAssert.Contains(json, key, $"missing {key}");
        // No PascalCase leak: a property without an attribute would show up under its C# name.
        Assert.IsFalse(json.Contains("\"Answer\"") || json.Contains("\"DocumentId\"") || json.Contains("\"LatencyMs\""));
    }

    [TestMethod]
    public void From_SerializedWithWebDefaults_ProducesTheSameNames()
    {
        var response = QueryResponse.From(Result([new Citation("doc1", "Title", "QC1", "rel/path", Page: 2)]));

        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        foreach (var key in ExpectedKeys)
            StringAssert.Contains(json, key, $"missing {key}");
    }

    // The result carries retrieval internals the eval reads (RetrievedDocumentRanking and, since
    // 2026-09-23, RetrievedRerankerScores - D228 step 3). They exist for the eval row, not for the
    // OutSystems frontend: exposing them would grow the payload and change the public contract
    // without a decision. From must not map them, under either host's serializer. Asserted as
    // "every key is a pinned name", so any future field added to QueryResponse without a
    // [JsonPropertyName] - or any internal that leaks through - fails here by name.
    [TestMethod]
    public void From_ExposesNoRetrievalInternals_EveryKeyIsAPinnedName()
    {
        var result = Result([new Citation("doc1", "Title", "QC1", "rel/path", Page: 2)]) with
        {
            ReferencesRetrieved      = 3,
            RetrievedDocumentRanking = ["doc1", "doc2", "doc1"],
            RetrievedRerankerScores  = [2.9f, 2.1f, null],
            ContextDocumentIds       = ["doc1", "doc1", "doc2"],
        };
        var pinned = ExpectedKeys.Select(k => k.Trim('"')).ToHashSet();

        foreach (var options in new[] { new JsonSerializerOptions(), new JsonSerializerOptions(JsonSerializerDefaults.Web) })
        {
            var json = JsonSerializer.Serialize(QueryResponse.From(result), options);
            using var doc = JsonDocument.Parse(json);
            var keys = new List<string>();
            CollectKeys(doc.RootElement, keys);

            var unexpected = keys.Where(k => !pinned.Contains(k)).Distinct().ToList();
            Assert.AreEqual(0, unexpected.Count, "keys not in the pinned contract: " + string.Join(", ", unexpected));
            Assert.IsFalse(json.Contains("rerank", StringComparison.OrdinalIgnoreCase) || json.Contains("ranking", StringComparison.OrdinalIgnoreCase)
                           || json.Contains("ContextDocument", StringComparison.OrdinalIgnoreCase),
                "retrieval internals leaked into the wire payload");
        }
    }

    private static void CollectKeys(JsonElement element, List<string> keys)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    keys.Add(property.Name);
                    CollectKeys(property.Value, keys);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectKeys(item, keys);
                break;
        }
    }

    [TestMethod]
    public void From_LabelIsTitleDashPage_AndUrlIsNullButPresent()
    {
        var response = QueryResponse.From(Result([new Citation("doc1", "Vilans protocollen voor neustampon", null, null, Page: 2)]));

        var source = response.Sources.Single();
        Assert.AreEqual("[Vilans protocollen voor neustampon] - p.2", source.Label);
        Assert.IsNull(source.Url);
        StringAssert.Contains(JsonSerializer.Serialize(response), "\"url\":null");
    }

    [TestMethod]
    public void From_NoPage_LabelFallsBackToTitle()
    {
        var response = QueryResponse.From(Result([new Citation("doc1", "Title", null, null)]));

        Assert.AreEqual("Title", response.Sources.Single().Label);
    }

    [TestMethod]
    public void From_CopiesCategoryAndTelemetry()
    {
        var response = QueryResponse.From(Result([], category: "privacy"));

        Assert.AreEqual("privacy", response.Category);
        Assert.AreEqual(123, response.Telemetry.LatencyMs);
        Assert.AreEqual(10,  response.Telemetry.InputTokens);
        Assert.AreEqual(20,  response.Telemetry.OutputTokens);
        Assert.AreEqual(0,   response.Sources.Count);
    }

    [TestMethod]
    public void QueryRunReportFactory_BlobPath_IsTheQueriesFolderTheFunctionsHostWritesTo()
    {
        var timestamp = new DateTimeOffset(2026, 9, 16, 14, 5, 9, TimeSpan.Zero);

        Assert.AreEqual("queries/2026/09/16/14-05-09.json", QueryRunReportFactory.BlobPath(timestamp));
    }

    [TestMethod]
    public void QueryRunReportFactory_Create_CopiesQuestionTimestampAndResultFields()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var result    = Result([], category: "buiten_scope");

        var report = QueryRunReportFactory.Create("Wat is de vraag?", timestamp, result);

        Assert.AreEqual("Wat is de vraag?", report.Question);
        Assert.AreEqual(timestamp,          report.Timestamp);
        Assert.AreEqual("conv-1",           report.RunId);
        Assert.AreEqual("Het antwoord",     report.Answer);
        Assert.AreEqual("buiten_scope",     report.Category);
        Assert.AreEqual(15,                 report.ContextTokens);
        Assert.AreEqual(30,                 report.TotalTokens);
    }
}
