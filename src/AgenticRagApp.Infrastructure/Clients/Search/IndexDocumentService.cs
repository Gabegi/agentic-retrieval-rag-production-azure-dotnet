using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Logging;
using AgenticRagApp.Infrastructure.Configuration;

namespace AgenticRagApp.Infrastructure.Clients.Search;

public class IndexDocumentService : IIndexDocumentService
{
    private readonly SearchClient                  _client;
    private readonly SearchIndexClient             _indexClient;
    private readonly IndexerConfig                 _config;
    private readonly ILogger<IndexDocumentService> _logger;

    public IndexDocumentService(
        IndexerConfig config, SearchClient client, SearchIndexClient indexClient, ILogger<IndexDocumentService> logger)
    {
        _client      = client;
        _indexClient = indexClient;
        _config      = config;
        _logger      = logger;
    }

    // Batches internally (1000 per call — the Search push API limit).
    public async Task<(int Succeeded, int Failed)> UpsertDocumentsAsync<T>(IEnumerable<T> documents, CancellationToken ct = default)
    {
        var succeeded = 0;
        var failed    = 0;
        var batches   = 0;

        foreach (var batch in documents.ToList().Chunk(1000))
        {
            batches++;
            var response = await _client.UploadDocumentsAsync(batch, cancellationToken: ct);
            foreach (var result in response.Value.Results)
            {
                if (result.Succeeded)
                {
                    succeeded++;
                }
                else
                {
                    _logger.LogWarning("Failed to upsert {Key}: {Error}", result.Key, result.ErrorMessage);
                    failed++;
                }
            }
        }

        _logger.LogInformation("Upsert complete — {Succeeded} succeeded, {Failed} failed ({Batches} batch(es))", succeeded, failed, batches);
        return (succeeded, failed);
    }

    // This is the "target" side of ExtractionService's new/updated/skipped diff - the one
    // thing that decides whether we pay Document Intelligence to (re-)extract a document.
    // A flat Size=1000 with no paging silently truncated this to the first 1000 CHUNKS
    // (not documents), which at real chunk-per-document ratios is reached after a few dozen
    // documents. Everything past that window then looks "not indexed" on every run and is
    // re-extracted (and re-billed) forever, with no error or log to say why.
    //
    // Paged by "id gt {lastSeenId}" (keyset/range pagination on the sortable key field), not
    // $skip: Azure AI Search caps a combined skip+top at 100,000, whereas keyset pagination
    // has no such ceiling - each page is an independent filtered query, not an offset into
    // the full result set. Requires "id" to be IsSortable (see IndexService's field list).
    public async Task<Dictionary<string, DateTimeOffset>> GetCurrentlyIndexedDocsIdsNDatesAsync(CancellationToken ct = default)
    {
        const int pageSize = 1000;

        var result = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        string? lastId = null;

        while (true)
        {
            var options = new SearchOptions
            {
                Select  = { "id", "document_id", "last_modified_date" },
                OrderBy = { "id" },
                Size    = pageSize,
            };
            if (lastId is not null)
                options.Filter = $"id gt '{lastId.Replace("'", "''")}'";

            var response  = await _client.SearchAsync<SearchDocument>("*", options, ct);
            var pageCount = 0;
            await foreach (var r in response.Value.GetResultsAsync().WithCancellation(ct))
            {
                pageCount++;
                if (r.Document.TryGetValue("id", out var idValue) && idValue is string chunkId)
                    lastId = chunkId;

                if (r.Document.TryGetValue("document_id",      out var idObj)   && idObj   is string docId &&
                    r.Document.TryGetValue("last_modified_date", out var dateObj) && dateObj is DateTimeOffset date)
                    result.TryAdd(docId, date);
            }

            // A short page means we've reached the end - a full page means there may be more.
            if (pageCount < pageSize) break;
        }

        _logger.LogInformation("Found {Count} documents currently in index", result.Count);
        return result;
    }

    // Batches document IDs into groups of 50 to keep the OData filter length manageable.
    public async Task<IReadOnlyList<string>> GetChunkIdsForDocumentsAsync(IEnumerable<string> documentIds, CancellationToken ct = default)
    {
        var idList = documentIds.ToList();
        if (idList.Count == 0) return [];

        var chunkIds = new List<string>();

        foreach (var batch in idList.Chunk(50))
        {
            var escaped = batch.Select(id => id.Replace("'", "''"));
            var filter  = $"search.in(document_id, '{string.Join(",", escaped)}', ',')";
            var options = new SearchOptions { Filter = filter, Select = { "id" }, Size = 1000 };

            var response = await _client.SearchAsync<SearchDocument>("*", options, ct);
            await foreach (var r in response.Value.GetResultsAsync().WithCancellation(ct))
            {
                if (r.Document.TryGetValue("id", out var idObj) && idObj is string chunkId)
                    chunkIds.Add(chunkId);
            }
        }

        return chunkIds;
    }

    public async Task<int> DeleteChunksByIdAsync(IEnumerable<string> chunkIds, CancellationToken ct = default)
    {
        var idList = chunkIds.ToList();
        if (idList.Count == 0) return 0;

        foreach (var batch in idList.Chunk(1000))
        {
            var actions = batch.Select(id => IndexDocumentsAction.Delete("id", id));
            await _client.IndexDocumentsAsync(IndexDocumentsBatch.Create(actions.ToArray()), cancellationToken: ct);
        }

        _logger.LogInformation("Deleted {ChunkCount} chunks", idList.Count);
        return idList.Count;
    }

    // Whole-index aggregate — lives on SearchIndexClient, not the per-document SearchClient
    // this class otherwise talks to.
    public async Task<(long DocumentCount, long StorageSizeBytes)> GetStatisticsAsync(CancellationToken ct = default)
    {
        var response = await _indexClient.GetIndexStatisticsAsync(_config.SearchIndexName, ct);
        return (response.Value.DocumentCount, response.Value.StorageSize);
    }
}
