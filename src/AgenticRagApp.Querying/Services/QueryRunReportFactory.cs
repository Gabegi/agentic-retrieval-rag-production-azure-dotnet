using AgenticRagApp.Observability.Reports;
using AgenticRagApp.Querying.Models;

namespace AgenticRagApp.Querying.Services;

// One place that turns a RagQueryResult into the per-query report, shared by the two hosts that
// serve POST /api/query (QueryingFunction, AgenticRagApp.Api's QueryEndpoint) - the same reason
// QueryResponse exists on the response side. The WRITING stays with the host, which owns
// IRunReportWriter and decides whether it is enabled; this only fixes the blob path and the
// field mapping so the two hosts cannot produce two report shapes.
public static class QueryRunReportFactory
{
    // pipeline-reports/queries/{yyyy}/{MM}/{dd}/{HH-mm-ss}.json - the path QueryingFunction has
    // written since the report existed; a second host must land in the same folder or the
    // per-query reports split by host.
    public static string BlobPath(DateTimeOffset timestamp) =>
        $"queries/{timestamp:yyyy/MM/dd}/{timestamp:HH-mm-ss}.json";

    public static QueryRunReport Create(string question, DateTimeOffset timestamp, RagQueryResult result) => new(
        RunId:              result.ConversationId,
        Timestamp:          timestamp,
        Question:           question,
        Answer:             result.Answer,
        RetrievedContext:   result.RetrievedContext,
        SystemInstructions: result.SystemInstructions,
        ChunksRetrieved:    result.ChunksRetrieved,
        OperationName:      result.OperationName,
        ProviderName:       result.ProviderName,
        ServerAddress:      result.ServerAddress,
        ServerPort:         result.ServerPort,
        ConversationId:     result.ConversationId,
        Model:              result.Model,
        FinishReason:       result.FinishReason,
        Category:           result.Category,
        LatencyMs:          result.LatencyMs,
        InputTokens:        result.InputTokens,
        OutputTokens:       result.OutputTokens,
        TotalTokens:        result.TotalTokens,
        ContextTokens:      result.ContextTokens,
        Temperature:        result.Temperature,
        MaxOutputTokens:    result.MaxOutputTokens,
        TopP:               result.TopP,
        TopK:               result.TopK,
        FrequencyPenalty:   result.FrequencyPenalty,
        PresencePenalty:    result.PresencePenalty,
        Seed:               result.Seed,
        ResponseFormat:     result.ResponseFormat,
        StopSequences:      result.StopSequences);
}
