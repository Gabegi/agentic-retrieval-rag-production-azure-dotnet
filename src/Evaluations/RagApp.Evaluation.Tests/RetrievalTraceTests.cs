using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticRagApp.Querying.Models;
using RagApp.Evaluation.Tests.Evaluation;
using RagApp.Evaluation.Tests.Models;

namespace RagApp.Evaluation.Tests;

// Offline - no judge, no knowledge base. Pins the two retrieval-trace columns on the eval row
// (2026-09-23, D228 step 3). RagEvaluator itself needs a judge client to build a row, so the
// formatting is a pure function (RetrievalTrace) and these tests drive it with the same
// RagQueryResult shape AgenticRagQueryService produces.
[TestClass]
public class RetrievalTraceTests
{
    private static RagQueryResult Result(IReadOnlyList<string>? ranking, IReadOnlyList<float?>? scores, IReadOnlyList<string>? contextIds = null) => new(
        Answer: "a", RetrievedContext: "ctx", SystemInstructions: "i", ChunksRetrieved: ranking?.Count ?? 0,
        OperationName: "op", ProviderName: "p", ServerAddress: "s", ServerPort: 443, ConversationId: "c",
        Model: "m", FinishReason: "stop", Category: null, LatencyMs: 1, InputTokens: 1, OutputTokens: 1,
        TotalTokens: 2, ContextTokens: 1, Temperature: null, MaxOutputTokens: null, TopP: null, TopK: null,
        FrequencyPenalty: null, PresencePenalty: null, Seed: null, ResponseFormat: null, StopSequences: null,
        Citations: [])
    {
        ReferencesRetrieved      = ranking?.Count ?? 0,
        RetrievedDocumentRanking = ranking,
        RetrievedRerankerScores  = scores,
        ContextDocumentIds       = contextIds,
    };

    [TestMethod]
    public void ContextDocuments_OneIdPerJudgedBlock_DuplicatesKept_EmptyWhenAbsent()
    {
        // Neighbours and hits of one document are separate blocks and stay separate ids; the
        // improve-side count for D228 step 4a is a lookup on this against the ranking.
        Assert.AreEqual("pdf/a.pdf | pdf/a.pdf | pdf/b.pdf", RetrievalTrace.ContextDocuments(Result(["pdf/a.pdf", "pdf/b.pdf"], [2f, 1f], ["pdf/a.pdf", "pdf/a.pdf", "pdf/b.pdf"])));
        Assert.AreEqual("", RetrievalTrace.ContextDocuments(Result(null, null)));
    }

    [TestMethod]
    public void Ranking_And_Scores_HaveOneEntryPerReference_InTheSameOrder_DuplicatesKept()
    {
        var result = Result(["pdf/a.pdf", "pdf/a.pdf", "pdf/b.pdf"], [2.913f, 2.64f, null]);

        Assert.AreEqual("pdf/a.pdf | pdf/a.pdf | pdf/b.pdf", RetrievalTrace.Ranking(result));
        Assert.AreEqual("2.91 | 2.64 | ",                    RetrievalTrace.Scores(result));
        Assert.AreEqual(3, RetrievalTrace.Ranking(result).Split(RetrievalTrace.Separator).Length);
        Assert.AreEqual(3, RetrievalTrace.Scores(result).Split(RetrievalTrace.Separator).Length);
    }

    [TestMethod]
    public void Scores_UseTheInvariantCulture()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("nl-NL");
            Assert.AreEqual("2.50", RetrievalTrace.Scores(Result(["pdf/a.pdf"], [2.5f])));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [TestMethod]
    public void NoRanking_GuardBlockedOrLegacyResult_IsEmptyNotNull()
    {
        var result = Result(null, null);

        Assert.AreEqual("", RetrievalTrace.Ranking(result));
        Assert.AreEqual("", RetrievalTrace.Scores(result));
    }

    // The equivalents-only row (gq-vocab-001 after D228 step 2: ExpectedSources empty,
    // EquivalentSources = the two language editions). The trace is label-independent - what was
    // retrieved is recorded whatever the row expects - and on the same result the rank metrics
    // read the equivalents as a hit. Loaded from the real dataset, so this test also fails if the
    // row is ever relabelled back.
    [TestMethod]
    public void EquivalentsOnlyRow_CarriesTheTrace_AndScoresTheEquivalentAsAHit()
    {
        var row = LoadGolden().Single(q => q.Name == "gq-vocab-001-emotionele-ontwikkeling-fasen");
        Assert.AreEqual("", row.ExpectedSources, "gq-vocab-001 is expected to be an equivalents-only row since D228 step 2");
        var booklet = row.EquivalentSources.Split(';', StringSplitOptions.TrimEntries)[0];
        StringAssert.StartsWith(booklet, "pdf/5d67ba6f");

        var result = Result(["pdf/other.pdf", booklet, booklet], [2.8f, 2.7f, 2.1f]);

        Assert.AreEqual($"pdf/other.pdf | {booklet} | {booklet}", RetrievalTrace.Ranking(result));
        Assert.AreEqual("2.80 | 2.70 | 2.10",                       RetrievalTrace.Scores(result));
        Assert.AreEqual(1.0, RetrievalRankMetrics.CitationMatch(row.ExpectedSources, row.EquivalentSources, result.RetrievedDocumentRanking!));
        Assert.AreEqual(2,   RetrievalRankMetrics.FirstRelevantRank(row.ExpectedSources, row.EquivalentSources, result.RetrievedDocumentRanking));
    }

    // EvalRow round-trips through the same serializer EvalResultWriter uses, with both columns
    // present by name - the jsonl readouts key on these names.
    [TestMethod]
    public void EvalRow_SerializesBothTraceColumnsByName()
    {
        var q   = new TestQuery("s", "d", "q", "a", "", "low", "v");
        var row = EvalRow.ForFailure(q, "boom", 1) with { RetrievedDocumentRanking = "pdf/a.pdf | pdf/b.pdf", RerankerScores = "2.90 | 1.10", ContextDocumentIds = "pdf/a.pdf | pdf/a.pdf" };

        var json = JsonSerializer.Serialize(row, new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } });

        StringAssert.Contains(json, "\"RetrievedDocumentRanking\":\"pdf/a.pdf | pdf/b.pdf\"");
        StringAssert.Contains(json, "\"RerankerScores\":\"2.90 | 1.10\"");
        StringAssert.Contains(json, "\"ContextDocumentIds\":\"pdf/a.pdf | pdf/a.pdf\"");
        StringAssert.Contains(json, "\"EquivalentSources\":\"\"");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static TestQuery[] LoadGolden() =>
        JsonSerializer.Deserialize<TestQuery[]>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "testdata", "golden-questions.json")), JsonOptions)
        ?? throw new InvalidOperationException("golden-questions.json did not deserialize");
}
