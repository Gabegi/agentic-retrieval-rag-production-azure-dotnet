using Azure;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Microsoft.Extensions.Logging;
using AgenticRagApp.Infrastructure.Configuration;

namespace AgenticRagApp.Infrastructure.Clients.Search;

// Schema is the union of every field either doc-type's chunks populate: PDF-only fields
// (table_count/has_table/figure_captions) sit alongside CSV-only fields
// (summary/department/quick_code/relative_path/check_date/version) in the same index.
// A field one doc-type never populates is simply left null/empty on that doc-type's rows
// — Azure AI Search doesn't require every document to set every field.
public class IndexService : IIndexService
{
    private readonly SearchIndexClient     _client;
    private readonly IndexerConfig         _config;
    private readonly ILogger<IndexService> _logger;

    public IndexService(IndexerConfig config, SearchIndexClient client, ILogger<IndexService> logger)
    {
        _config = config;
        _client = client;
        _logger = logger;
    }

    // Creates the index on first run. Skips if it already exists - see the class comment
    // above for why.
    public async Task EnsureIndexAsync()
    {
        var index   = BuildIndexDefinition(BuildVectorSearch(), BuildSemanticSearch());
        var created = await EnsureIndexExistsAsync(index);
        _logger.LogInformation(created ? "Index '{Name}' created" : "Index '{Name}' already exists — skipping creation", _config.SearchIndexName);
    }

    public async Task RecreateIndexAsync()
    {
        var deleted = await DeleteIndexIfExistsAsync(_config.SearchIndexName);
        _logger.LogWarning(deleted
            ? "Index '{Name}' deleted — all previously indexed documents are gone until a restore or reindex repopulates it"
            : "Index '{Name}' didn't exist to delete", _config.SearchIndexName);

        var index = BuildIndexDefinition(BuildVectorSearch(), BuildSemanticSearch());
        await EnsureIndexExistsAsync(index);
        _logger.LogInformation("Index '{Name}' recreated empty", _config.SearchIndexName);
    }

    // Get-or-create only — never updates an existing index (avoids a code-driven push
    // silently overwriting portal-side customisation). Returns true if it was created,
    // false if it already existed.
    private async Task<bool> EnsureIndexExistsAsync(SearchIndex definition, CancellationToken ct = default)
    {
        try
        {
            await _client.GetIndexAsync(definition.Name, ct);
            return false;
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }

        await _client.CreateOrUpdateIndexAsync(definition, cancellationToken: ct);
        return true;
    }

    // Deletes the index outright, all documents included. Returns false (no-op, not an
    // error) if it didn't exist.
    private async Task<bool> DeleteIndexIfExistsAsync(string indexName, CancellationToken ct = default)
    {
        try
        {
            await _client.DeleteIndexAsync(indexName, ct);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }

    // Assembles the full index schema: fields, vector search config, and semantic search config.
    private SearchIndex BuildIndexDefinition(VectorSearch vectorSearch, SemanticSearch semanticSearch) =>
        new SearchIndex(_config.SearchIndexName)
        {
            Description = "Internal knowledge base for Cordaan (Dutch elderly and disability care " +
              "organization). Contains the full text of organizational documents: care and " +
              "quality protocols, work instructions, job descriptions (functiebeschrijvingen), " +
              "HR policies, facility and safety plans, financial procedures, privacy/security " +
              "policies, and software manuals (e.g. ONS/ECD, CIS). Use this index for questions " +
              "about Cordaan's internal policies, procedures, role responsibilities, and " +
              "care-related instructions.",
            VectorSearch   = vectorSearch,
            SemanticSearch = semanticSearch,
            Fields =
            {
                // NOTE: populated as a composite key at write time, e.g. {documentId}_p{pageNumber}_c{chunkIndex}.
                // DOCUMENT_ID alone is not unique per chunk — using it raw would make later chunks overwrite earlier ones.
                // IsSortable: lets SearchDocumentStore.GetCurrentIndexedDocumentDatesAsync page by
                // "id gt {lastSeenId}" (keyset pagination) instead of $skip, which is capped at a
                // combined skip+top of 100,000 - keyset pagination has no such ceiling since each
                // page is an independent filtered query, not an offset into the full result set.
                new SimpleField("id",                 SearchFieldDataType.String)         { IsKey = true, IsFilterable = true, IsSortable = true },
                // Raw DOCUMENT_ID (one value shared by all chunks of the same document).
                // Used by IndexDocumentService to query and batch-delete all chunks for a given document.
                new SimpleField("document_id",        SearchFieldDataType.String)         { IsFilterable = true },
                new SearchableField("title")                                               { IsFilterable = true, IsFacetable = true },
                new SearchableField("content")                                             { AnalyzerName = "nl.microsoft" },
                // CSV-only — curated "what is this document for" line from Zenya's SUMMARY
                // column. Kept out of "content" so it doesn't repeat once per chunk. Null for PDF rows.
                new SearchableField("summary")                                             { AnalyzerName = "nl.microsoft" },
                // Breadcrumb (from the PDF's bookmark outline) or DI-detected heading for
                // this page - see ChunkingService. PDF and CSV each populate this independently.
                new SearchableField("heading")                                             { IsFilterable = true, IsFacetable = true, AnalyzerName = "nl.microsoft" },
                // CSV-only — from FOLDER_MINI_FULL_PATH, bounded set of department/category
                // values (HR, Kwaliteit, Facilitaire zaken, ...). Null for PDF rows.
                new SearchableField("department")                                          { IsFilterable = true, IsFacetable = true },
                // CSV-only — from QUICK_CODE, Cordaan's internal document code. Null for PDF rows.
                new SimpleField("quick_code",         SearchFieldDataType.String)         { IsFilterable = true },
                // CSV-only — from RELATIVE_PATH, path back to the original source file.
                // Structured provenance metadata, not free-text search material. Null for PDF rows.
                new SimpleField("relative_path",      SearchFieldDataType.String)         { IsFilterable = true },
                // The blob's own storage LastModified (PDF) or LAST_MODIFIED_DATETIME (CSV).
                new SimpleField("last_modified_date", SearchFieldDataType.DateTimeOffset) { IsFilterable = true, IsSortable = true },
                // PDF-only — the PDF's own native Info-dictionary CreationDate/ModDate
                // (PdfNativeMetadataExtractor). ModDate is the real "is this policy current"
                // signal (when the content was actually last edited), distinct from
                // last_modified_date above (blob re-upload timing). Null for CSV rows.
                new SimpleField("created_at",         SearchFieldDataType.DateTimeOffset) { IsFilterable = true, IsSortable = true },
                new SimpleField("mod_date",           SearchFieldDataType.DateTimeOffset) { IsFilterable = true, IsSortable = true },
                // PDF-only — native page count (PdfNativeMetadataExtractor). Null for CSV rows.
                new SimpleField("page_count",         SearchFieldDataType.Int32)         { IsFilterable = true },
                // PDF-only — Zenya's own identity/lifecycle facts, sourced from custom blob
                // metadata set by whoever uploads the PDF (Zenya doesn't export these into the
                // PDF itself - see ZenyaMetadata's comment). Null until that metadata is set.
                new SimpleField("zenya_document_id", SearchFieldDataType.String)          { IsFilterable = true },
                new SimpleField("zenya_version",     SearchFieldDataType.String)          { IsFilterable = true },
                new SimpleField("zenya_status",       SearchFieldDataType.String)         { IsFilterable = true, IsFacetable = true },
                new SimpleField("zenya_url",          SearchFieldDataType.String)         { },
                // CSV-only — from CHECK_DATE, the next review/expiry date. Null for PDF rows.
                new SimpleField("check_date",         SearchFieldDataType.DateTimeOffset) { IsFilterable = true, IsSortable = true },
                // CSV-only — VERSION.REVISION (e.g. "7.0"). Null for PDF rows.
                new SimpleField("version",            SearchFieldDataType.String)         { IsFilterable = true },
                new SimpleField("page_number",        SearchFieldDataType.Int32),
                new SimpleField("chunk_index",        SearchFieldDataType.Int32),

                // PDF-only — Document Intelligence structural signals
                // (docs/chunking-rewrite-plan.md's Tier 2). Null/default for CSV rows.
                new SimpleField("table_count",        SearchFieldDataType.Int32)          { IsFilterable = true },
                new SimpleField("has_table",          SearchFieldDataType.Boolean)        { IsFilterable = true, IsFacetable = true },
                new SearchableField("figure_captions", collection: true)                   { AnalyzerName = "nl.microsoft" },

                new VectorSearchField("content_vector", _config.OpenAiEmbeddingDimensions, "vector-profile") { IsHidden = true, IsStored = false }
            }
        };

    // Configures HNSW vector search with an Azure OpenAI vectorizer for automatic query embedding at search time.
    private VectorSearch BuildVectorSearch()
    {
        var vectorSearch = new VectorSearch();
        vectorSearch.Algorithms.Add(new HnswAlgorithmConfiguration("hnsw-config"));
        vectorSearch.Profiles.Add(new VectorSearchProfile("vector-profile", "hnsw-config")
        {
            VectorizerName = "openai-vectorizer"
        });
        vectorSearch.Vectorizers.Add(new AzureOpenAIVectorizer("openai-vectorizer")
        {
            Parameters = new AzureOpenAIVectorizerParameters
            {
                ResourceUri    = new Uri(_config.OpenAiEndpoint.TrimEnd('/')),
                DeploymentName = _config.OpenAiEmbeddingDeployment,
                ModelName      = _config.OpenAiEmbeddingModelName
            }
        });
        return vectorSearch;
    }

    // Configures semantic ranking: title in TitleField (not KeywordsFields), content/summary
    // as primary, heading as keyword.
    private static SemanticSearch BuildSemanticSearch()
    {
        var semanticSearch = new SemanticSearch();
        semanticSearch.Configurations.Add(new SemanticConfiguration("semantic-config", new SemanticPrioritizedFields
        {
            TitleField     = new SemanticField("title"),
            ContentFields  = { new SemanticField("content"), new SemanticField("summary") },
            KeywordsFields = { new SemanticField("heading") }
        }));
        semanticSearch.DefaultConfigurationName = "semantic-config";
        return semanticSearch;
    }
}
