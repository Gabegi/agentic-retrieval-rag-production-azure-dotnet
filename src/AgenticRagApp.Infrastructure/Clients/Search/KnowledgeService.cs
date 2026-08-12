using Azure;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.KnowledgeBases.Models;
using Microsoft.Extensions.Logging;
using AgenticRagApp.Infrastructure.Configuration;

namespace AgenticRagApp.Infrastructure.Clients.Search;

public class KnowledgeService : IKnowledgeService
{
    private readonly SearchIndexClient        _client;
    private readonly IndexerConfig            _config;
    private readonly ILogger<KnowledgeService> _logger;

    public KnowledgeService(
        IndexerConfig              config,
        SearchIndexClient          client,
        ILogger<KnowledgeService>  logger)
    {
        _client = client;
        _config = config;
        _logger = logger;
    }

    public async Task EnsureKnowledgeSourceAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Creating knowledge source '{Name}'", _config.KnowledgeSourceName);

        var knowledgeSource = new SearchIndexKnowledgeSource(
            name: _config.KnowledgeSourceName,
            searchIndexParameters: new SearchIndexKnowledgeSourceParameters(_config.SearchIndexName)
            {
                // Limit BM25 to fields that carry semantic meaning. The CSV-era fields
                // (summary, department) are gone - PDF and CSV no longer share an index
                // (action-plan.md B2). heading_path is included as well as heading_text:
                // the chain is the context §1.6 wants contributing to scoring, not just a
                // label on the row.
                SearchFields =
                {
                    new SearchIndexFieldReference("content"),
                    new SearchIndexFieldReference("title"),
                    new SearchIndexFieldReference("heading_text"),
                    new SearchIndexFieldReference("heading_path"),
                },
                // All structured fields returned so the model has full document context
                SourceDataFields =
                {
                    new SearchIndexFieldReference("id"),
                    new SearchIndexFieldReference("document_id"),
                    new SearchIndexFieldReference("title"),
                    new SearchIndexFieldReference("heading_text"),
                    new SearchIndexFieldReference("heading_path"),
                    new SearchIndexFieldReference("content"),
                    // The parent section's text, materialized on the child - so an answer
                    // can be generated over the whole section while retrieval stayed at the
                    // precise child grain (Q3 option 1).
                    new SearchIndexFieldReference("parent_text"),
                    // Two-grain identity: section_id de-duplicates children of one section,
                    // grain says which cut this row is.
                    new SearchIndexFieldReference("section_id"),
                    new SearchIndexFieldReference("grain"),
                    // Sector/family - the wrong-population failure mode is invisible to any
                    // similarity score, so the model needs to see which sector a passage is
                    // about, not just that it is on topic.
                    new SearchIndexFieldReference("domain_tag"),
                    new SearchIndexFieldReference("family_id"),
                    // page_start/child_index — needed for query-time neighboring-page
                    // expansion in ChunkNeighborExpander (page-boundary continuations).
                    new SearchIndexFieldReference("page_start"),
                    new SearchIndexFieldReference("page_end"),
                    new SearchIndexFieldReference("child_index"),
                    // Native PDF metadata (PdfNativeMetadataExtractor) — page_count for
                    // "page X of Y" citations, created_at/mod_date so a citation can show
                    // how current a policy is. Null for CSV rows.
                    new SearchIndexFieldReference("page_count"),
                    new SearchIndexFieldReference("created_at"),
                    new SearchIndexFieldReference("mod_date"),
                    // Zenya provenance (IndexService's zenya_* fields). KnowledgeBaseReference-
                    // Mapper reads all four, but they were missing here, so every Citation came
                    // back with null document id/version/status/url — silently, since the mapper
                    // TryGetValue's them. That leaves a citation with no link back to Zenya and
                    // no way for CitationMatch to resolve an expected source by document id.
                    new SearchIndexFieldReference("zenya_document_id"),
                    new SearchIndexFieldReference("zenya_version"),
                    new SearchIndexFieldReference("zenya_status"),
                    new SearchIndexFieldReference("zenya_url"),
                }
                // note: content_vector is excluded — not needed for LLM context
            }
        )
        {
            Description = "Knowledge source for Zenya corporate document index"
        };

        await _client.CreateOrUpdateKnowledgeSourceAsync(knowledgeSource, onlyIfUnchanged: false, ct);
        _logger.LogInformation("Knowledge source '{Name}' created or updated", _config.KnowledgeSourceName);
    }

    public async Task EnsureKnowledgeBaseAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Creating knowledge base '{Name}'", _config.KnowledgeBaseName);

        var aoaiParams = new AzureOpenAIVectorizerParameters
        {
            ResourceUri    = new Uri(_config.OpenAiEndpoint),
            DeploymentName = _config.OpenAiGptDeployment,
            ModelName      = _config.OpenAiGptModelName
        };

        var knowledgeBase = new KnowledgeBase(
            name: _config.KnowledgeBaseName,
            knowledgeSources: new[] { new KnowledgeSourceReference(_config.KnowledgeSourceName) }
        )
        {
            Description = "Contains Zenya corporate documents including procedures, guidelines, " +
                          "and policies. Use this index to answer questions about document content, " +
                          "processes, responsibilities, and organizational procedures.",

            RetrievalInstructions = "Search for documents by title, topic, or document type. " +
                                    "Always cite the document title and source file in your answer. " +
                                    "For process or procedure questions, look for the relevant procedure document. " +
                                    "If multiple documents are relevant, discuss each separately.",

            AnswerInstructions = "Provide a complete and accurate answer based on the document content. " +
                                 "Always mention which document the information comes from. " +
                                 "Do not summarize or omit steps from procedures or guidelines. " +
                                 "If multiple documents are relevant, discuss each one separately. " +
                                 // B1, not C1 (changed 2026-08-12). The audience is the whole
                                 // workforce - care staff, facilities, students, non-native Dutch
                                 // speakers - not policy authors. B1 is also the Dutch standard for
                                 // public-facing communication. Note this contradicts criterion 1 in
                                 // AcceptatieCriteria.md as written ("C1-level Dutch"); that document
                                 // needs updating with the PO.
                                 "Write the answer in simple Dutch at CEFR B1 level, so anyone can " +
                                 "understand it: short sentences, everyday words, one idea per sentence, " +
                                 "active voice. Avoid jargon and abbreviations; when a term from the " +
                                 "document is unavoidable, use it and explain it in plain words. " +
                                 "Do not simplify by leaving things out - the answer must stay complete " +
                                 "and accurate, and every step of a procedure must still be there. " +
                                 "State only what the source documents say - never give a personal or " +
                                 "subjective opinion. If asked for one, say explicitly that you can only " +
                                 "share what the documentation says, not an opinion. " +
                                 // Real Dutch copy from the golden-questions dataset (2026-08-06) - see
                                 // docs/2608/260806/po-open-questions.md. This is the wording-only half of
                                 // criterion 6; hard enforcement (AgenticRagQueryService.AskAsync's
                                 // initialChunks.Count == 0 check) is separate and covers the case this
                                 // instruction might miss.
                                 "If none of the retrieved documents actually answer the question, say so " +
                                 "plainly rather than guessing or padding the answer: respond with " +
                                 "\"Hier kan ik geen antwoord op geven. Vraag dit na bij je leidinggevende.\" " +
                                 // Criterion 2 - distinct text from the one above, matched to the dataset's
                                 // medisch_advies category. Instruction-only: no code-level guard for this
                                 // one (see po-open-questions.md's open questions on whether one is wanted).
                                 "If the question asks for medical advice - a diagnosis, a treatment or " +
                                 "medication decision for a specific client, or a triage/urgency judgment - " +
                                 "do not answer it, even if a protocol document seems to touch on the topic. " +
                                 "Respond with: \"Deze vraag kan ik niet beantwoorden. Ik geef alleen " +
                                 "informatie over zorgprotocollen en geen medisch advies. Neem bij twijfel " +
                                 "over een cliënt altijd contact op met een zorgprofessional.\"",

            OutputMode               = KnowledgeRetrievalOutputMode.AnswerSynthesis,
            RetrievalReasoningEffort = new KnowledgeRetrievalMediumReasoningEffort(),
            Models                   = { new KnowledgeBaseAzureOpenAIModel(aoaiParams) }
        };

        await _client.CreateOrUpdateKnowledgeBaseAsync(knowledgeBase, onlyIfUnchanged: false, ct);
        _logger.LogInformation("Knowledge base '{Name}' created or updated", _config.KnowledgeBaseName);
    }

    public async Task DeleteKnowledgeBaseAsync(CancellationToken ct = default)
    {
        var deleted = await DeleteKnowledgeBaseIfExistsAsync(_config.KnowledgeBaseName, ct);
        _logger.LogWarning(deleted
            ? "Knowledge base '{Name}' deleted"
            : "Knowledge base '{Name}' didn't exist to delete", _config.KnowledgeBaseName);
    }

    public async Task DeleteKnowledgeSourceAsync(CancellationToken ct = default)
    {
        var deleted = await DeleteKnowledgeSourceIfExistsAsync(_config.KnowledgeSourceName, ct);
        _logger.LogWarning(deleted
            ? "Knowledge source '{Name}' deleted"
            : "Knowledge source '{Name}' didn't exist to delete", _config.KnowledgeSourceName);
    }

    // Deletes outright. Returns false (no-op, not an error) if it didn't exist - same
    // shape as IIndexService.RecreateIndexAsync's own delete step.
    private async Task<bool> DeleteKnowledgeBaseIfExistsAsync(string name, CancellationToken ct)
    {
        try
        {
            await _client.DeleteKnowledgeBaseAsync(name, ct);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }

    private async Task<bool> DeleteKnowledgeSourceIfExistsAsync(string name, CancellationToken ct)
    {
        try
        {
            await _client.DeleteKnowledgeSourceAsync(name, ct);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }
}
