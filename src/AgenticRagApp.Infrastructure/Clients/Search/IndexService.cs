using Azure;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Microsoft.Extensions.Logging;
using AgenticRagApp.Infrastructure.Configuration;

namespace AgenticRagApp.Infrastructure.Clients.Search;

// The PDF chunk schema. It used to be the union of both doc-types' fields, with CSV-only
// columns (summary/department/quick_code/relative_path/check_date/version) sitting
// permanently null on every PDF row. Those are gone: PDF and CSV share nothing here now
// (docs/2608/260812/action-plan.md B2), and the CSV pipeline is not wired into the
// FunctionApp at all - no trigger, no DI registration.
//
// Fields whose producer does not exist yet are still declared. That is deliberate: the schema
// is applied as ONE migration rather than one per item that lands, because adding a field later
// means another rebuild of the index. It is also why a producer-less field is not evidence of a
// bug - reviewed on 260818 and kept, deliberately, against the alternative of dropping them and
// paying for a second rebuild if a producer arrives (chunking-done.md §17 item 7).
//
// The list has shrunk since that rule was written, so it is named rather than left as "...":
//   - population           - genuinely unproduced. A client-group axis (LVB/MVB), distinct from
//                            domain_tag, and it needs a vocabulary nobody has settled yet.
//   - grain                - stamped, but a constant "child": the parent/document grains it
//                            distinguishes are not emitted by any route today.
// section_id and page_extraction_flag were on this list and are no longer - both are produced by
// ChunkMetadataBuilder. Anything added here should be removed from it the moment that changes,
// or the comment starts excusing fields that have no excuse.
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
            Description = "Internal knowledge base for Contoso (Dutch elderly and disability care " +
              "organization). Contains the full text of organizational documents: care and " +
              "quality protocols, work instructions, job descriptions (functiebeschrijvingen), " +
              "HR policies, facility and safety plans, financial procedures, privacy/security " +
              "policies, and software manuals (e.g. ONS/ECD, CIS). Use this index for questions " +
              "about Contoso's internal policies, procedures, role responsibilities, and " +
              "care-related instructions.",
            VectorSearch   = vectorSearch,
            SemanticSearch = semanticSearch,
            Fields =
            {
                // ── Identity and position (docs/2608/260812/action-plan.md §4.6) ────────
                // Naming rule: *_id names a thing, *_index names a position within an
                // explicitly stated scope. No bare "ordinal", no bare "index" - three fields
                // previously meant three different things under near-identical names
                // (Ordinal = page, ChunkIndex = position-within-page, Index =
                // position-within-strategy-output).
                //
                // id is base64({document_id}::s{section_index}::c{child_index}) - stable
                // against pagination changes, unlike the old page-number-bearing key where
                // inserting one page shifted every subsequent id.
                // IsSortable: lets SearchDocumentStore.GetCurrentIndexedDocumentDatesAsync page by
                // "id gt {lastSeenId}" (keyset pagination) instead of $skip, which is capped at a
                // combined skip+top of 100,000 - keyset pagination has no such ceiling since each
                // page is an independent filtered query, not an offset into the full result set.
                new SimpleField("id",                 SearchFieldDataType.String)         { IsKey = true, IsFilterable = true, IsSortable = true },
                // Raw DOCUMENT_ID (one value shared by all chunks of the same document).
                // Used by IndexDocumentService to query and batch-delete all chunks for a given document.
                new SimpleField("document_id",        SearchFieldDataType.String)         { IsFilterable = true },
                // The parent section this unit belongs to. On a parent unit this equals its
                // own id, so "everything in this section" is one filter with no special case.
                // Null until the section grain exists (Phase E) - the field is defined now so
                // the schema migrates once, not once per item that lands.
                new SimpleField("section_id",         SearchFieldDataType.String)         { IsFilterable = true },
                // Position of the section within its document.
                new SimpleField("section_index",      SearchFieldDataType.Int32)          { IsFilterable = true, IsSortable = true },
                // Position of the child within its section. 0 on a parent unit.
                new SimpleField("child_index",        SearchFieldDataType.Int32)          { IsFilterable = true, IsSortable = true },
                // "document" | "parent" | "child" - explicit rather than inferred from
                // whether section_id == id. Filterable because Q3 option 2 (parents indexed
                // but not embedded) excludes parents from ranking with exactly this filter.
                new SimpleField("grain",              SearchFieldDataType.String)         { IsFilterable = true, IsFacetable = true },

                new SearchableField("title")                                               { IsFilterable = true, IsFacetable = true },
                new SearchableField("content")                                             { AnalyzerName = "nl.microsoft" },
                // The whole parent section's text, materialized onto each child (Q3 option 1).
                // STORED ONLY - deliberately not searchable and not filterable: it repeats a
                // section's text once per child (fan-out ~1.25, measured in Phase A), so
                // indexing it would inflate the index and skew BM25 term frequencies by
                // counting a section's terms once per child rather than once.
                new SimpleField("parent_text",        SearchFieldDataType.String)         { },
                // This unit's own heading, leaf only. Was "heading".
                new SearchableField("heading_text")                                        { IsFilterable = true, IsFacetable = true, AnalyzerName = "nl.microsoft" },
                // The full heading chain ("Hoofdstuk 3 > 3.2 Dosering"). Searchable because
                // it is the context §1.6 wants contributing to BM25, not just a label.
                new SearchableField("heading_path")                                        { IsFilterable = true, AnalyzerName = "nl.microsoft" },
                // H1-H6 nesting level (Heading.Depth).
                new SimpleField("heading_depth",      SearchFieldDataType.Int32)          { IsFilterable = true, IsFacetable = true },
                // "di_heading" | "bookmark" | "di_section" | "none" - breadcrumbs and DI
                // headings have different provenance, and DI's nested sections are a third.
                new SimpleField("heading_source",     SearchFieldDataType.String)         { IsFilterable = true, IsFacetable = true },
                // The blob's own storage LastModified.
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

                // ── Pages ──────────────────────────────────────────────────────────────
                // A unit can span pages once sections are the grain, so one page number is
                // not enough. Both derived from the document's PageSpan map, so exact rather
                // than inferred. Replaces the old "page_number".
                new SimpleField("page_start",         SearchFieldDataType.Int32)          { IsFilterable = true, IsSortable = true },
                new SimpleField("page_end",           SearchFieldDataType.Int32)          { IsFilterable = true, IsSortable = true },

                // ── Size ───────────────────────────────────────────────────────────────
                // Both stored per unit: the token count cannot be reconstructed from
                // char_count later, because chars/token is not constant (prose ~3.1-3.3,
                // table markdown ~1.9-2.8 - re-measured 260812, see ChunkingHelper).
                new SimpleField("char_count",         SearchFieldDataType.Int32)          { IsFilterable = true },
                new SimpleField("token_count",        SearchFieldDataType.Int32)          { IsFilterable = true },

                // Where this chunk sits in the document's CLEANED text. Retrievable, never
                // filtered: their job is the offset round-trip invariant (content must equal
                // the source sliced at these coordinates) and the query-time structural window,
                // which reassembles a section by slicing the source rather than by re-reading
                // neighbouring chunks. Nothing narrows a search by them.
                new SimpleField("chunk_start",        SearchFieldDataType.Int32)          { },
                new SimpleField("chunk_length",       SearchFieldDataType.Int32)          { },

                // ── How this chunk was produced ────────────────────────────────────────
                // Which of the two routes cut it, and how the document was sized. Facetable
                // because the question they answer is distributional - "are Large documents
                // ending up on the recursive route", which is the density test rejecting real
                // structure - and that is a facet query, not an investigation.
                new SimpleField("route_name",         SearchFieldDataType.String)         { IsFilterable = true, IsFacetable = true },
                new SimpleField("size_class",         SearchFieldDataType.String)         { IsFilterable = true, IsFacetable = true },

                // ── Document validity ──────────────────────────────────────────────────
                // Parsed from the title, which is where this corpus states it ("CAO GGZ 2024
                // 2026"). Filterable and sortable because the question is "is this still in
                // force" - a superseded CAO answering a current question is the same class of
                // failure as a wrong-sector answer, and equally invisible to a similarity
                // score. Null when the title carries no period: absent, never guessed.
                new SimpleField("valid_from",         SearchFieldDataType.DateTimeOffset) { IsFilterable = true, IsSortable = true },
                new SimpleField("valid_to",           SearchFieldDataType.DateTimeOffset) { IsFilterable = true, IsSortable = true },
                new SimpleField("version",            SearchFieldDataType.String)         { IsFilterable = true },

                // ── Identity / ambiguity ───────────────────────────────────────────────
                // The sector-ambiguity failure mode returns a well-formed, on-topic,
                // WRONG-POPULATION chunk that no similarity score can flag. A metadata
                // filter is the only deterministic fix, so these must be filterable - they
                // were computed by DocumentIdentityResolver and carried on ChunkObject already,
                // but never reached the index.
                new SimpleField("family_id",          SearchFieldDataType.String)         { IsFilterable = true, IsFacetable = true },
                // Sector code from the title (DomainTagger): GGZ/GHZ/VGZ/VVT. This IS the
                // "sector" field - not a separate one.
                new SimpleField("domain_tag",         SearchFieldDataType.String)         { IsFilterable = true, IsFacetable = true },
                // Documents whose titles are lexically close but NOT the same family
                // (Medido/Medimo) - a confusion flag, not a family relationship.
                new SimpleField("confusable_with",    SearchFieldDataType.Collection(SearchFieldDataType.String)) { IsFilterable = true },
                // Target population (LVB/MVB and similar). Distinct from domain_tag: sector
                // and population are different axes. No producer yet.
                new SimpleField("population",         SearchFieldDataType.String)         { IsFilterable = true, IsFacetable = true },
                // "nl"/"en" from DI's own AnalyzeResult.Languages. The corpus is Dutch plus
                // one English document whose chars/token ratio is ~4, not ~3.2 - which makes
                // every character-derived ceiling wrong for it.
                new SimpleField("language",           SearchFieldDataType.String)         { IsFilterable = true, IsFacetable = true },

                // ── Document Intelligence structural signals ───────────────────────────
                new SimpleField("table_count",        SearchFieldDataType.Int32)          { IsFilterable = true },
                new SimpleField("has_table",          SearchFieldDataType.Boolean)        { IsFilterable = true, IsFacetable = true },
                new SearchableField("figure_captions", collection: true)                   { AnalyzerName = "nl.microsoft" },

                // ── Quality flags ──────────────────────────────────────────────────────
                // This child carries overlap from a sibling - makes retrieval-time
                // de-duplication cheap without re-comparing text.
                new SimpleField("is_overlap",         SearchFieldDataType.Boolean)        { IsFilterable = true },
                // Whether this unit's heading was located by a confident match or fell back.
                // The per-chunk form of the heading-locator's failure counter: the aggregate
                // says how many failed, this says which chunks to distrust.
                new SimpleField("heading_located",    SearchFieldDataType.Boolean)        { IsFilterable = true },
                // Set when this unit's pages include figure-only / zero-word pages - the
                // document-level extraction gate cannot see a mixed document.
                new SimpleField("page_extraction_flag", SearchFieldDataType.Boolean)      { IsFilterable = true },

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

    // Configures semantic ranking: title in TitleField (not KeywordsFields), content as
    // primary, heading text and chain as keywords.
    //
    // "summary" is gone from ContentFields - it was CSV-only and always null on PDF rows
    // (action-plan.md B2). parent_text is deliberately NOT here either: it repeats a
    // section's text once per child, so ranking on it would score the same passage
    // repeatedly under different ids.
    private static SemanticSearch BuildSemanticSearch()
    {
        var semanticSearch = new SemanticSearch();
        semanticSearch.Configurations.Add(new SemanticConfiguration("semantic-config", new SemanticPrioritizedFields
        {
            TitleField     = new SemanticField("title"),
            ContentFields  = { new SemanticField("content") },
            KeywordsFields = { new SemanticField("heading_text"), new SemanticField("heading_path") }
        }));
        semanticSearch.DefaultConfigurationName = "semantic-config";
        return semanticSearch;
    }
}
