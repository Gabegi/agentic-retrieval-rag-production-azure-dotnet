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
                // Limit BM25 to fields that carry semantic meaning
                SearchFields =
                {
                    new SearchIndexFieldReference("content"),
                    new SearchIndexFieldReference("summary"),
                    new SearchIndexFieldReference("title"),
                    new SearchIndexFieldReference("heading"),
                    new SearchIndexFieldReference("department"),
                },
                // All structured fields returned so the model has full document context
                SourceDataFields =
                {
                    new SearchIndexFieldReference("id"),
                    new SearchIndexFieldReference("document_id"),
                    new SearchIndexFieldReference("title"),
                    new SearchIndexFieldReference("heading"),
                    new SearchIndexFieldReference("department"),
                    new SearchIndexFieldReference("quick_code"),
                    new SearchIndexFieldReference("relative_path"),
                    new SearchIndexFieldReference("version"),
                    new SearchIndexFieldReference("content"),
                    new SearchIndexFieldReference("summary"),
                    // page_number/chunk_index — needed for query-time neighboring-page
                    // expansion in ChunkNeighborExpander (page-boundary continuations).
                    new SearchIndexFieldReference("page_number"),
                    new SearchIndexFieldReference("chunk_index"),
                    // Native PDF metadata (PdfNativeMetadataExtractor) — page_count for
                    // "page X of Y" citations, created_at/mod_date so a citation can show
                    // how current a policy is. Null for CSV rows.
                    new SearchIndexFieldReference("page_count"),
                    new SearchIndexFieldReference("created_at"),
                    new SearchIndexFieldReference("mod_date"),
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
                                 "If multiple documents are relevant, discuss each one separately.",

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
