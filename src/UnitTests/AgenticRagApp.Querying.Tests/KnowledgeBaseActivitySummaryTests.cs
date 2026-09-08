using System.ClientModel.Primitives;
using Azure.Search.Documents.KnowledgeBases.Models;
using AgenticRagApp.Querying.Services;

namespace RagApp.UnitTests.Querying;

[TestClass]
public class KnowledgeBaseActivitySummaryTests
{
    // These are Azure SDK response-only models (no public constructor) - built via
    // ModelReaderWriter from JSON, the SDK's documented pattern for constructing them in tests.
    private static KnowledgeBaseModelQueryPlanningActivityRecord PlanningRecord(long? input, long? output) =>
        ModelReaderWriter.Read<KnowledgeBaseModelQueryPlanningActivityRecord>(BinaryData.FromString(
            $$"""{"type":"modelQueryPlanning","inputTokens":{{input?.ToString() ?? "null"}},"outputTokens":{{output?.ToString() ?? "null"}}}"""))!;

    private static KnowledgeBaseModelAnswerSynthesisActivityRecord SynthesisRecord(long? input, long? output) =>
        ModelReaderWriter.Read<KnowledgeBaseModelAnswerSynthesisActivityRecord>(BinaryData.FromString(
            $$"""{"type":"modelAnswerSynthesis","inputTokens":{{input?.ToString() ?? "null"}},"outputTokens":{{output?.ToString() ?? "null"}}}"""))!;

    // "type":"searchIndex" is the discriminator the SDK writes for this record, and the query
    // text sits on searchIndexArguments.search - both verified against the 11.8.0-beta.1
    // serializer rather than assumed, since the whole sub-query measurement rests on them.
    private static KnowledgeBaseSearchIndexActivityRecord SearchRecord(string? search) =>
        ModelReaderWriter.Read<KnowledgeBaseSearchIndexActivityRecord>(BinaryData.FromString(
            search is null
                ? """{"type":"searchIndex","id":1,"searchIndexArguments":{}}"""
                : $$$"""{"type":"searchIndex","id":1,"searchIndexArguments":{"search":{{{System.Text.Json.JsonSerializer.Serialize(search)}}}}}"""))!;

    [TestMethod]
    public void NullActivity_ReturnsZeroTokens()
    {
        var (input, output) = KnowledgeBaseActivitySummary.SumTokens(null);

        Assert.AreEqual(0, input);
        Assert.AreEqual(0, output);
    }

    [TestMethod]
    public void EmptyActivity_ReturnsZeroTokens()
    {
        var (input, output) = KnowledgeBaseActivitySummary.SumTokens([]);

        Assert.AreEqual(0, input);
        Assert.AreEqual(0, output);
    }

    [TestMethod]
    public void PlanningRecord_TokensAreSummed()
    {
        var record = PlanningRecord(10, 20);

        var (input, output) = KnowledgeBaseActivitySummary.SumTokens([record]);

        Assert.AreEqual(10, input);
        Assert.AreEqual(20, output);
    }

    [TestMethod]
    public void SynthesisRecord_TokensAreSummed()
    {
        var record = SynthesisRecord(5, 7);

        var (input, output) = KnowledgeBaseActivitySummary.SumTokens([record]);

        Assert.AreEqual(5, input);
        Assert.AreEqual(7, output);
    }

    [TestMethod]
    public void MultipleRecords_TokensAcrossPlanningAndSynthesisAreSummed()
    {
        var planning  = PlanningRecord(10, 20);
        var synthesis = SynthesisRecord(5, 7);

        var (input, output) = KnowledgeBaseActivitySummary.SumTokens([planning, synthesis]);

        Assert.AreEqual(15, input);
        Assert.AreEqual(27, output);
    }

    [TestMethod]
    public void NullTokenValues_AreTreatedAsZero()
    {
        var record = PlanningRecord(null, null);

        var (input, output) = KnowledgeBaseActivitySummary.SumTokens([record]);

        Assert.AreEqual(0, input);
        Assert.AreEqual(0, output);
    }

    [TestMethod]
    public void CollectSubQueries_NullActivity_ReturnsEmpty()
    {
        Assert.AreEqual(0, KnowledgeBaseActivitySummary.CollectSubQueries(null).Count);
    }

    [TestMethod]
    public void CollectSubQueries_ReturnsSearchTextsInActivityOrder()
    {
        var activity = new KnowledgeBaseActivityRecord[]
        {
            PlanningRecord(10, 20),
            SearchRecord("bewaartermijn camerabeelden zorgdomotica"),
            SearchRecord("DPIA verplicht nieuwe verwerking"),
            SynthesisRecord(5, 7),
        };

        var subQueries = KnowledgeBaseActivitySummary.CollectSubQueries(activity);

        CollectionAssert.AreEqual(
            new[] { "bewaartermijn camerabeelden zorgdomotica", "DPIA verplicht nieuwe verwerking" },
            subQueries.ToArray());
    }

    // A single search means the planning step produced one ordinary query - the case that
    // says agentic retrieval did nothing for this question, so the count has to come back
    // as 1 rather than as "no activity reported".
    [TestMethod]
    public void CollectSubQueries_SingleSearch_CountsAsOne()
    {
        var subQueries = KnowledgeBaseActivitySummary.CollectSubQueries(
            [PlanningRecord(10, 20), SearchRecord("hoe moet ik mij ziekmelden")]);

        Assert.AreEqual(1, subQueries.Count);
    }

    [TestMethod]
    public void CollectSubQueries_RecordsWithoutSearchText_AreSkipped()
    {
        var subQueries = KnowledgeBaseActivitySummary.CollectSubQueries(
            [SearchRecord(null), SearchRecord("   "), SearchRecord("koeltemperatuur")]);

        CollectionAssert.AreEqual(new[] { "koeltemperatuur" }, subQueries.ToArray());
    }

    [TestMethod]
    public void CollectSubQueries_NoSearchRecords_ReturnsEmpty()
    {
        var subQueries = KnowledgeBaseActivitySummary.CollectSubQueries(
            [PlanningRecord(10, 20), SynthesisRecord(5, 7)]);

        Assert.AreEqual(0, subQueries.Count);
    }
}
