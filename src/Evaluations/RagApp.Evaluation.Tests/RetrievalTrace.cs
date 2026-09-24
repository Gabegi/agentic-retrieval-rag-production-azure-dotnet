using System.Globalization;
using AgenticRagApp.Querying.Models;

namespace RagApp.Evaluation.Tests.Evaluation;

// Formats the retrieved set for the eval row (2026-09-23, D228 step 3): the k reference document
// ids in the service's reranker order and the score behind each, as two ' | '-joined strings so
// the jsonl stays one flat record per row for the awk/python readouts D181 uses.
//
// Pure, so it can be tested without a judge; RagEvaluator calls it from both row branches. It
// reads RagQueryResult.RetrievedDocumentRanking / RetrievedRerankerScores, which
// AgenticRagQueryService fills from the same ordering, and which QueryResponse.From never maps -
// this trace exists on the eval row and nowhere on the wire.
public static class RetrievalTrace
{
    public const string Separator = " | ";

    // "" when the result carries no ranking (guard-blocked, or a result from before the field).
    public static string Ranking(RagQueryResult result) =>
        string.Join(Separator, result.RetrievedDocumentRanking ?? []);

    // Same length and order as Ranking; a blank entry where the service sent no score. F2 with
    // the invariant culture, so "2.91" never becomes "2,91" on a Dutch agent.
    public static string Scores(RagQueryResult result) =>
        string.Join(Separator, (result.RetrievedRerankerScores ?? [])
            .Select(s => s?.ToString("F2", CultureInfo.InvariantCulture) ?? ""));

    // One document id per block of RetrievedContext, block order (D228 step 3b). "" when the
    // result carries none (guard-blocked, or a result from before the field).
    public static string ContextDocuments(RagQueryResult result) =>
        string.Join(Separator, result.ContextDocumentIds ?? []);
}
