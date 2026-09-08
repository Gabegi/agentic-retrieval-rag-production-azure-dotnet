using Azure.Search.Documents.KnowledgeBases.Models;

namespace AgenticRagApp.Querying.Services;

// Query-planning + answer-synthesis token usage lives in per-step activity records
// rather than a rolled-up total on the response, so sum them here.
public static class KnowledgeBaseActivitySummary
{
    public static (long InputTokens, long OutputTokens) SumTokens(IEnumerable<KnowledgeBaseActivityRecord>? activity)
    {
        long input = 0, output = 0;
        if (activity is null)
            return (input, output);

        foreach (var record in activity)
        {
            switch (record)
            {
                case KnowledgeBaseModelQueryPlanningActivityRecord planning:
                    input  += planning.InputTokens ?? 0;
                    output += planning.OutputTokens ?? 0;
                    break;
                case KnowledgeBaseModelAnswerSynthesisActivityRecord synthesis:
                    input  += synthesis.InputTokens ?? 0;
                    output += synthesis.OutputTokens ?? 0;
                    break;
            }
        }
        return (input, output);
    }

    /// <summary>
    /// The search queries the knowledge base actually issued against the index for one
    /// retrieve, in activity order.
    /// </summary>
    /// <remarks>
    /// This is the only direct evidence of what agentic retrieval did with a question. The
    /// knowledge base plans its own searches and reports one
    /// <see cref="KnowledgeBaseSearchIndexActivityRecord"/> per search it ran, each carrying
    /// the query text it used. One record with the question echoed back means the planning
    /// step cost tokens and produced a single ordinary search; several records with different
    /// texts mean it decomposed the question, which is the behaviour a plain vector search
    /// cannot reproduce.
    ///
    /// Without this, "did agentic retrieval add value" can only be answered by comparing
    /// answer scores against a separate non-agentic run - which conflates planning, the
    /// synthesis model, and retrieval into one number. The per-row count separates them:
    /// a decomposition-labelled question answered from one search was never actually given
    /// the chance to benefit.
    /// </remarks>
    public static IReadOnlyList<string> CollectSubQueries(IEnumerable<KnowledgeBaseActivityRecord>? activity) =>
        activity?
            .OfType<KnowledgeBaseSearchIndexActivityRecord>()
            .Select(r => r.SearchIndexArguments?.Search)
            .Where(q => !string.IsNullOrWhiteSpace(q))
            .Select(q => q!)
            .ToList()
        ?? [];
}
