using System.Diagnostics;
using Azure.Search.Documents.KnowledgeBases.Models;
using Microsoft.Extensions.Logging;
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
    // The buiten_scope fallback constant lived here until 2026-08-12, paired with the
    // zero-chunk guard in AskAsync. Both were removed together by request. The same Dutch
    // wording is still produced - by the model, via KnowledgeService's AnswerInstructions -
    // so the dataset's expected text is unchanged; only the deterministic enforcement is gone.

    private const string CategoryPrivacy        = "privacy";
    private const string CategoryPromptInjectie = "promptinjectie";

    private readonly IKnowledgeRetrievalClient _client;
    private readonly ChunkNeighborExpander     _neighborExpander;
    private readonly IPromptInjectionGuard     _injectionGuard;
    private readonly IPiiGuard                 _piiGuard;
    private readonly IndexerConfig             _config;
    private readonly ILogger<AgenticRagQueryService> _logger;

    public AgenticRagQueryService(
        IndexerConfig                  config,
        IKnowledgeRetrievalClient      client,
        ChunkNeighborExpander          neighborExpander,
        IPromptInjectionGuard          injectionGuard,
        IPiiGuard                      piiGuard,
        ILogger<AgenticRagQueryService> logger)
    {
        _client           = client;
        _neighborExpander = neighborExpander;
        _injectionGuard   = injectionGuard;
        _piiGuard         = piiGuard;
        _config           = config;
        _logger           = logger;
    }

    public async Task<RagQueryResult> AskAsync(string question, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        // The index is NFC throughout (PdfCleaner normalizes bodies; ExtractedTextRepair
        // covers titles and headings), so the question must be too - a user typing a
        // decomposed "ë" (common from macOS keyboards) would otherwise miss every lexical
        // match on the very term they typed.
        question = question.Normalize(System.Text.NormalizationForm.FormC);

        // Criterion 5, question side. Must run before the retrieve call, otherwise the PII
        // has already left the process.
        // NOTE: under GuardsLogOnly this no longer stops the question being sent, so PII in a
        // question does reach Azure AI Search. That is the one guard whose log-only mode is not
        // merely "answer anyway" - it changes what leaves the process. See IndexerConfig.
        if (await _piiGuard.ContainsPiiAsync(question, ct) && Enforce("pii", "question"))
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
            // Without these the service returns references with SourceData null, and
            // KnowledgeBaseReferenceMapper drops every one of them at its `r.SourceData is null`
            // guard - so initialChunks.Count == 0 and the answer is produced with no grounded
            // context at all, however good the retrieval was.
            // That is what the 2026-08-11 eval hit: 31 of 32 answerable golden questions refused,
            // back when a criterion-6 guard turned that condition into the buiten-scope fallback.
            // That guard was removed 2026-08-12, so the same fault would now surface as an
            // ungrounded answer instead - see the ChunksRetrieved == 0 warning below.
            // A live retrieve on 2026-08-12 returned 13 references and a correct, fully
            // synthesised Dutch answer with sourceData null on all 13 - the knowledge base was
            // working the whole time and this method was discarding its output.
            // These are per-request (KnowledgeSourceParams), not part of the knowledge base
            // definition: KnowledgeService cannot set them once at deploy time, so they have to
            // be sent on every retrieve. See docs/2608/260812/knowledgebasefix-action-plan.md.
            KnowledgeSourceParams =
            {
                new SearchIndexKnowledgeSourceParams(_config.KnowledgeSourceName)
                {
                    IncludeReferences          = true,
                    IncludeReferenceSourceData = true,
                },
            },
        };

        var result = await _client.RetrieveAsync(request, ct);

        var initialChunks = KnowledgeBaseReferenceMapper.Map(result.References);

        // Criterion 6's *enforcement* half was removed here on 2026-08-12 by request: zero
        // mapped chunks no longer short-circuits to BuitenScopeFallback. The knowledge base's
        // own answer is returned instead, whatever it says.
        // Criterion 6 is not unguarded - AnswerInstructions still tells the model to reply
        // "Hier kan ik geen antwoord op geven. Vraag dit na bij je leidinggevende." when the
        // retrieved documents don't answer the question (KnowledgeService.cs). What is gone is
        // the deterministic floor underneath that instruction, so an out-of-scope question now
        // gets a refusal only if the model chooses to give one.
        // Removing the guard also removed the finish reasons no_relevant_answer and
        // references_unmappable, which were what distinguished "search matched nothing" from
        // "the mapper dropped every reference" - the exact 2026-08-11 fault (13 references in,
        // 0 mapped). Both now return an ordinary FinishReason: stop row carrying an ungrounded
        // answer, so neither is visible in any aggregate. This log is the replacement: it keeps
        // the two causes distinguishable at query time even though the response no longer says
        // which happened. In the eval report, the equivalent signal is ChunksRetrieved == 0 on
        // an answered row. See docs/2608/260812/knowledgebasefix-action-plan.md.
        if (initialChunks.Count == 0)
        {
            var referenceCount = result.References?.Count ?? 0;
            _logger.LogWarning(
                "Answering with no grounded context: {ReferenceCount} reference(s) returned, 0 mapped - {Cause}.",
                referenceCount,
                referenceCount == 0
                    ? "search matched nothing (index or knowledge-source side)"
                    : "every reference was dropped by the mapper, so SourceData had no usable 'content' - " +
                      "check AgenticRagQueryService still sends KnowledgeSourceParams with IncludeReferenceSourceData");
        }

        var chunks = await _neighborExpander.ExpandAsync(initialChunks, ct);

        // Criterion 4. Prompt Shields analyzes userPrompt and documents together in one
        // call, so this runs once, after retrieval, covering both direct injection (in
        // question) and indirect/document-embedded injection (in the retrieved chunks) -
        // no separate pre-retrieval call. The only cost of checking after retrieval rather
        // than before is a wasted read-only Search query on a blocked request, not any
        // actual exposure.
        if (await _injectionGuard.IsAttackAsync(question, chunks, ct) && Enforce("injection", "question+documents"))
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
        if (await _piiGuard.ContainsPiiAsync(answer, ct) && Enforce("pii", "answer"))
            return Blocked(PiiFallback, "blocked_pii", CategoryPrivacy, sw.ElapsedMilliseconds, chunks.Count);

        // Numeric grounding: a figure in the answer that appears in none of the retrieved
        // chunks is a claim from model memory wearing this context's citations (the 260818
        // eval's "8,33% vakantietoeslag" case - correct number, fabricated attribution).
        // Log-only by design: excising sentences from Dutch prose breaks the answer, and the
        // eval records the same measurement per scenario so the class fails a run visibly.
        var ungrounded = NumericGroundingGuard.FindUngrounded(answer, string.Join("\n", chunks));
        if (ungrounded.Count > 0)
            _logger.LogWarning(
                "Answer asserts {Count} number(s) absent from the retrieved context: {Numbers}",
                ungrounded.Count, string.Join(", ", ungrounded));

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

    // Called only once a guard has already fired. Always logs; returns whether the caller
    // should actually block. Written as a condition so each call site reads as
    // "guard tripped AND we're enforcing" and the short-circuit keeps the log out of the
    // path where nothing tripped.
    //
    // Log-only mode exists so one eval run can measure how often each guard fires on the real
    // corpus before anyone tunes thresholds - two of these three had never executed at all
    // before 2026-08-12, so their false-positive rate is unmeasured rather than known-bad.
    // The trade is that while it is on, criteria 4 and 5 are observed but not enforced.
    private bool Enforce(string guard, string scope)
    {
        if (_config.GuardsLogOnly)
        {
            _logger.LogWarning(
                "Guard '{Guard}' ({Scope}) fired but GuardsLogOnly is set - answering anyway. " +
                "Acceptance criterion is NOT enforced for this request.", guard, scope);
            return false;
        }

        _logger.LogWarning("Guard '{Guard}' ({Scope}) fired - blocking the request.", guard, scope);
        return true;
    }

    // inputTokens/outputTokens default to 0 for the guard paths that block before any model
    // work happens (PII on the question side); the post-retrieval blocks pass the real
    // counts, so a blocked row still shows what the call actually cost.
    private RagQueryResult Blocked(
        string fallbackAnswer, string finishReason, string category, long latencyMs,
        int chunksRetrieved = 0, long inputTokens = 0, long outputTokens = 0)
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
            InputTokens:        inputTokens,
            OutputTokens:       outputTokens,
            TotalTokens:        inputTokens + outputTokens,
            ContextTokens:      0,
            Temperature:        null, MaxOutputTokens: null, TopP: null, TopK: null,
            FrequencyPenalty:   null, PresencePenalty: null, Seed: null,
            ResponseFormat:     null, StopSequences: null,
            Citations:          Array.Empty<Citation>());
    }
}
