using System.Diagnostics;
using Azure.Search.Documents.KnowledgeBases.Models;
using AgenticRagApp.Infrastructure.Clients.KnowledgeRetrieval;
using AgenticRagApp.Infrastructure.Configuration;
using AgenticRagApp.Querying.Guards;
using AgenticRagApp.Querying.Models;

namespace AgenticRagApp.Querying.Services;

// Agentic retrieval through the Azure AI Search knowledge base created by
// KnowledgeService. The knowledge base decomposes the question into one or more
// search queries and synthesizes the final answer (AnswerSynthesis), so no
// separate chat completion call is made here. Reference parsing, neighboring-page
// expansion, and token accounting are delegated to KnowledgeBaseReferenceMapper,
// ChunkNeighborExpander, and KnowledgeBaseActivitySummary — this class only
// orchestrates the call and assembles the result. Guard checks (prompt injection,
// PII — acceptance criteria 4 & 5) run here rather than as instructions to the
// knowledge base, since those must hold regardless of what the model decides to do;
// see AcceptatieCriteria.md.
public class AgenticRagQueryService : IRagQueryService
{
    // Real Dutch copy from the golden-questions dataset (2026-08-06), not placeholders -
    // see docs/2608/260806/po-open-questions.md. Text is matched exactly to that dataset's
    // expected answers so eval scoring lines up.
    private const string PiiFallback       = "Ik kan geen vragen verwerken met persoonlijke gegevens. Verwijder namen, adressen of andere persoonsgegevens en probeer het opnieuw.";
    private const string InjectionFallback = "Hier kan ik geen antwoord op geven.";
    // Distinct, longer text for the "nothing relevant found" case (buiten_scope) - the
    // dataset keeps this separate from InjectionFallback above even though both start the
    // same way.
    private const string BuitenScopeFallback = "Hier kan ik geen antwoord op geven. Vraag dit na bij je leidinggevende.";

    private const string CategoryPrivacy        = "privacy";
    private const string CategoryPromptInjectie = "promptinjectie";
    private const string CategoryBuitenScope    = "buiten_scope";

    private readonly IKnowledgeRetrievalClient _client;
    private readonly ChunkNeighborExpander     _neighborExpander;
    private readonly IPromptInjectionGuard     _injectionGuard;
    private readonly IPiiGuard                 _piiGuard;
    private readonly IndexerConfig             _config;

    public AgenticRagQueryService(
        IndexerConfig                  config,
        IKnowledgeRetrievalClient      client,
        ChunkNeighborExpander          neighborExpander,
        IPromptInjectionGuard          injectionGuard,
        IPiiGuard                      piiGuard)
    {
        _client           = client;
        _neighborExpander = neighborExpander;
        _injectionGuard   = injectionGuard;
        _piiGuard         = piiGuard;
        _config           = config;
    }

    public async Task<RagQueryResult> AskAsync(string question, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        // Criterion 5, question side. Must run before the retrieve call, otherwise the PII
        // has already left the process.
        if (await _piiGuard.ContainsPiiAsync(question, ct))
            return Blocked(PiiFallback, "blocked_pii", CategoryPrivacy, sw.ElapsedMilliseconds);

        var request = new KnowledgeBaseRetrievalRequest
        {
            Messages =
            {
                new KnowledgeBaseMessage(new KnowledgeBaseMessageContent[]
                {
                    new KnowledgeBaseMessageTextContent(question),
                })
                { Role = "user" },
            },
            IncludeActivity = true,
        };

        var result = await _client.RetrieveAsync(request, ct);

        var initialChunks = KnowledgeBaseReferenceMapper.Map(result.References);

        // Criterion 6 enforcement (buiten_scope in the golden-questions dataset). Zero
        // documents matched at all - checked before neighbor-page expansion runs, since
        // there's nothing to expand neighbors of. See po-open-questions.md's proposal: a
        // relevance-score threshold would catch more (a few weakly-relevant chunks that
        // still produce a padded answer) but needs a reranker score this codebase doesn't
        // map anywhere yet, plus eval data to calibrate a cutoff - the zero-chunks floor
        // needs neither.
        if (initialChunks.Count == 0)
            return Blocked(BuitenScopeFallback, "no_relevant_answer", CategoryBuitenScope, sw.ElapsedMilliseconds);

        var chunks = await _neighborExpander.ExpandAsync(initialChunks, ct);

        // Criterion 4. Prompt Shields analyzes userPrompt and documents together in one
        // call, so this runs once, after retrieval, covering both direct injection (in
        // question) and indirect/document-embedded injection (in the retrieved chunks) -
        // no separate pre-retrieval call. The only cost of checking after retrieval rather
        // than before is a wasted read-only Search query on a blocked request, not any
        // actual exposure.
        if (await _injectionGuard.IsAttackAsync(question, chunks, ct))
            return Blocked(InjectionFallback, "blocked_injection", CategoryPromptInjectie, sw.ElapsedMilliseconds, chunks.Count);

        // One citation per distinct (document, page) among the direct hits — grouping by
        // document alone would collapse an answer that cites page 2 and page 5 of the same
        // document down to just one of those pages.
        var citations = initialChunks
            .GroupBy(c => (c.DocumentId, c.Page))
            .Select(g => new Citation(
                g.Key.DocumentId, g.First().Title, g.First().QuickCode, g.First().RelativePath,
                g.First().ZenyaDocumentId, g.First().ZenyaVersion, g.First().ZenyaStatus, g.First().ZenyaUrl,
                g.Key.Page, g.First().PageCount, g.First().CreatedAt, g.First().ModDate))
            .ToList();

        var answer = string.Join("\n", result.Response
            .SelectMany(m => m.Content)
            .OfType<KnowledgeBaseMessageTextContent>()
            .Select(c => c.Text));

        // Criterion 5, answer side. Documents themselves may contain client data. Replaced
        // wholesale rather than redacted in place — a partially redacted Dutch sentence
        // usually reads as broken — and citations are dropped too, since we're not showing
        // sources for a suppressed answer.
        if (await _piiGuard.ContainsPiiAsync(answer, ct))
            return Blocked(PiiFallback, "blocked_pii", CategoryPrivacy, sw.ElapsedMilliseconds, chunks.Count);

        var (inputTokens, outputTokens) = KnowledgeBaseActivitySummary.SumTokens(result.Activity);
        var retrievedContext = string.Join("\n\n---\n\n", chunks);

        var endpoint = new Uri(_config.SearchEndpoint);
        return new RagQueryResult(
            Answer:             answer,
            RetrievedContext:   retrievedContext,
            SystemInstructions: "knowledge-base retrieval/answer instructions — see KnowledgeService",
            ChunksRetrieved:    chunks.Count,
            OperationName:      "knowledge_base_retrieve",
            ProviderName:       "azure_ai_search",
            ServerAddress:      endpoint.Host,
            ServerPort:         endpoint.Port,
            ConversationId:     Guid.NewGuid().ToString("N"),
            Model:              _config.OpenAiGptModelName,
            FinishReason:       "stop",
            Category:           null,
            LatencyMs:          sw.ElapsedMilliseconds,
            InputTokens:        inputTokens,
            OutputTokens:       outputTokens,
            TotalTokens:        inputTokens + outputTokens,
            ContextTokens:      ContextTokenEstimator.Estimate(retrievedContext),
            Temperature:        null, MaxOutputTokens: null, TopP: null, TopK: null,
            FrequencyPenalty:   null, PresencePenalty: null, Seed: null,
            ResponseFormat:     null, StopSequences: null,
            Citations:          citations);
    }

    private RagQueryResult Blocked(string fallbackAnswer, string finishReason, string category, long latencyMs, int chunksRetrieved = 0)
    {
        var endpoint = new Uri(_config.SearchEndpoint);
        return new RagQueryResult(
            Answer:             fallbackAnswer,
            RetrievedContext:   string.Empty,
            SystemInstructions: "knowledge-base retrieval/answer instructions — see KnowledgeService",
            ChunksRetrieved:    chunksRetrieved,
            OperationName:      "knowledge_base_retrieve",
            ProviderName:       "azure_ai_search",
            ServerAddress:      endpoint.Host,
            ServerPort:         endpoint.Port,
            ConversationId:     Guid.NewGuid().ToString("N"),
            Model:              _config.OpenAiGptModelName,
            FinishReason:       finishReason,
            Category:           category,
            LatencyMs:          latencyMs,
            InputTokens:        0,
            OutputTokens:       0,
            TotalTokens:        0,
            ContextTokens:      0,
            Temperature:        null, MaxOutputTokens: null, TopP: null, TopK: null,
            FrequencyPenalty:   null, PresencePenalty: null, Seed: null,
            ResponseFormat:     null, StopSequences: null,
            Citations:          Array.Empty<Citation>());
    }
}
