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
    private readonly SearchRequestByteCounter?     _requestBytes;

    // requestBytes: the counter the SearchClient's request-size policy feeds (D203 §8). Optional
    // so a client built without the policy - tests, tools - still works; BytesSent is then null.
    public IndexDocumentService(
        IndexerConfig config, SearchClient client, SearchIndexClient indexClient, ILogger<IndexDocumentService> logger,
        SearchRequestByteCounter? requestBytes = null)
    {
        _client       = client;
        _indexClient  = indexClient;
        _config       = config;
        _logger       = logger;
        _requestBytes = requestBytes;
    }

    // Batches internally (1000 per call — the Search push API limit).
    public async Task<UpsertResult> UpsertDocumentsAsync<T>(IEnumerable<T> documents, CancellationToken ct = default)
    {
        var succeeded = 0;
        var failed    = 0;
        var batches   = 0;
        var durations = new List<long>();
        var before    = _requestBytes?.Read();

        foreach (var batch in documents.ToList().Chunk(1000))
        {
            batches++;
            // The SDK call alone - serialisation happens inside it, so this is the whole cost of
            // the batch as this code experiences it, not the service-side indexing time.
            var clock    = System.Diagnostics.Stopwatch.StartNew();
            var response = await _client.UploadDocumentsAsync(batch, cancellationToken: ct);
            clock.Stop();
            durations.Add(clock.ElapsedMilliseconds);
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

        // The payload, as the pipeline saw it leave. Trusted only when the counter moved by
        // exactly this call's batches with every length computable; anything else is null, not
        // an estimate - see UpsertResult.
        long? bytesSent = null;
        if (before is { } b && _requestBytes is not null)
        {
            var after = _requestBytes.Read();
            var requests   = after.IndexDocsRequests   - b.IndexDocsRequests;
            var unmeasured = after.IndexDocsUnmeasured - b.IndexDocsUnmeasured;
            if (requests == batches && unmeasured == 0)
                bytesSent = after.IndexDocsBytes - b.IndexDocsBytes;
            else
                _logger.LogWarning(
                    "Upsert payload not attributable: {Requests} push requests counted against {Batches} batches, {Unmeasured} without a computable length",
                    requests, batches, unmeasured);
        }

        _logger.LogInformation("Upsert complete — {Succeeded} succeeded, {Failed} failed ({Batches} batch(es), {Bytes} bytes sent)",
            succeeded, failed, batches, bytesSent?.ToString() ?? "unmeasured");
        return new UpsertResult(succeeded, failed, batches, durations, bytesSent);
    }

    // Same batching as the upsert above, different action: MergeDocuments overwrites only the
    // fields the payload carries. See IIndexDocumentService for why it is Merge and not
    // MergeOrUpload, and why the payload type matters.
    //
    // A failure here is logged per key and counted rather than thrown, matching the upsert: the
    // caller (UploadService) has already put replacement content live by this point, and a failed
    // patch leaves the previous value in place rather than corrupting the row.
    public async Task<(int Succeeded, int Failed)> MergeDocumentFieldsAsync<T>(IEnumerable<T> patches, CancellationToken ct = default)
    {
        var patchList = patches.ToList();
        if (patchList.Count == 0) return (0, 0);

        var succeeded = 0;
        var failed    = 0;

        foreach (var batch in patchList.Chunk(1000))
        {
            var response = await _client.MergeDocumentsAsync(batch, cancellationToken: ct);
            foreach (var result in response.Value.Results)
            {
                if (result.Succeeded)
                {
                    succeeded++;
                }
                else
                {
                    // Most likely cause is a key the index does not hold, which means the caller's
                    // view of what is indexed has drifted from the index itself - worth the key in
                    // the log, since that is what makes it chaseable.
                    _logger.LogWarning("Failed to merge fields onto {Key}: {Error}", result.Key, result.ErrorMessage);
                    failed++;
                }
            }
        }

        _logger.LogInformation("Field merge complete — {Succeeded} succeeded, {Failed} failed", succeeded, failed);
        return (succeeded, failed);
    }

    // This is the "target" side of ExtractionService's new/updated/skipped diff - the one
    // thing that decides whether we pay Content Understanding to (re-)extract a document.
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

        // Diagnostics for D200 §6e: this method returned 0 against an index holding 3,734
        // documents, every incremental run therefore re-extracted the whole corpus at full
        // Content Understanding cost, and the report could not say WHICH of the two possible
        // causes it was. rawHits separates them with no extra request: 0 means the query matched
        // nothing, >0 with an empty result means every row failed the document_id +
        // last_modified_date pair below. firstDocShape then names the field and the CLR type that
        // did it. Both are only logged when the read comes back empty.
        //
        // The cause was found the same day by other means (D200 §6h: the SDK returns the date as
        // a string, see the pair check below) and fixed, so this should now fire only on a
        // genuinely empty index. Kept: it costs nothing, and an empty read is exactly the event
        // that needs its shape recorded when it does happen.
        var     rawHits      = 0;
        string? firstDocShape = null;

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
                rawHits++;
                firstDocShape ??= string.Join(", ", r.Document.Select(kv =>
                    $"{kv.Key}={(kv.Value is null ? "null" : kv.Value.GetType().Name)}"));

                if (r.Document.TryGetValue("id", out var idValue) && idValue is string chunkId)
                    lastId = chunkId;

                // GetDateTimeOffset, never `is DateTimeOffset` (2026-09-17, D200 §6h). Over the wire
                // the SDK (11.8.0-beta.1) hands a DateTimeOffset field back as a STRING -
                // "2026-08-06T13:46:46Z" - and the pattern match that stood here matched none of
                // them, so this method returned an empty map against a populated index on every
                // run. Every source document then compared as new: runs 9/260917/4 and /5 each
                // re-extracted all 51 documents (871 billed pages) for nothing, and reported
                // success. Proven on the service's actual bytes in IndexStateReadWireTests; the
                // in-test SearchDocuments of IndexDocumentServiceTests carry CLR values and could
                // never have seen it.
                //
                // ContainsKey gates the accessor: GetDateTimeOffset throws KeyNotFoundException on
                // an ABSENT member and returns null for a PRESENT null, and both must stay
                // "dateless row, skip" (the tests below pin that a dateless row is ignored, never
                // treated as oldest). The string check on document_id is fine as it was - strings
                // are the one type the SDK does hand back as themselves.
                if (r.Document.TryGetValue("document_id", out var idObj) && idObj is string docId &&
                    r.Document.ContainsKey("last_modified_date") &&
                    r.Document.GetDateTimeOffset("last_modified_date") is { } stamped)
                {
                    // A document-level rollup over per-chunk rows, so it needs a rule for rows
                    // that disagree. OLDEST wins: a document is current only if EVERY indexed row
                    // is current (2026-09-17, D199 A2).
                    //
                    // This was TryAdd, i.e. first-seen wins, which - ordered by chunk id - meant
                    // whichever chunk sorted first decided for the whole document. In the healthy
                    // case that is invisible, because DocumentStamp puts ONE date on every chunk
                    // of a document, so oldest == first == newest. It only matters when rows
                    // disagree, and the ways they can disagree are exactly the ways this codebase
                    // leaves a row behind: a chunk Search refused (DocsFailed), or one
                    // UploadService withheld. Under first-seen those documents read as fully
                    // current forever and were never reprocessed - the stale row could not heal.
                    //
                    // Leans on this scan being exhaustive, which it is: keyset pagination on
                    // "id gt lastId" with no $skip ceiling, terminating only on a short page. A
                    // capped scan would reintroduce the same bug in a subtler form, since a
                    // document whose oldest row fell past the cut would read current again.
                    //
                    // What it still cannot reach: a chunk with NO row in the index - withheld on
                    // its first ever run, or after its id changed - contributes no old date, so
                    // its siblings' fresh date wins and the document reads current. Withholding is
                    // non-damaging in that case but not self-healing; closing it needs a persisted
                    // worklist of withheld document ids, not a date rule (D199 §3/A2).
                    if (!result.TryGetValue(docId, out var seen) || stamped < seen)
                        result[docId] = stamped;
                }
            }

            // A short page means we've reached the end - a full page means there may be more.
            if (pageCount < pageSize) break;
        }

        _logger.LogInformation("Found {Count} documents currently in index", result.Count);

        // Only on the empty read - the case that costs a full re-extraction. Names the branch and
        // the index the client is actually bound to: IndexDocumentService takes a SearchClient
        // bound once at startup to IndexerConfig.SearchIndexName, while the query side resolves
        // the live generation through ICurrentIndexNameProvider, so a promoted generation would
        // leave this method reading a different index than the one being queried. That is a
        // hypothesis, not a diagnosis - this line is what turns it into one either way.
        if (result.Count == 0)
            _logger.LogWarning(
                "Index-state read returned no documents — raw hits {RawHits}, index '{IndexName}' (configured '{ConfiguredIndexName}'), first row shape: {Shape}. "
                + "Raw hits 0 = the query matched nothing; raw hits > 0 = every row failed the document_id + last_modified_date check, and the shape says which field. See D200 §6e.",
                rawHits, _client.IndexName, _config.SearchIndexName, firstDocShape ?? "(no rows returned)");

        return result;
    }

    // Batches document IDs into groups of 50 to keep the OData filter length manageable.
    //
    // Within each batch, paged by "id gt {lastSeenId}" exactly like
    // GetCurrentlyIndexedDocsIdsNDatesAsync above, and for the same reason: a flat Size=1000
    // capped the result at the first 1000 CHUNKS. Both callers of this method delete or patch
    // exactly the rows it returns and then report success - so every chunk past the cap
    // stayed in the index as a stale row (old content, or the wrong family_id) that nothing
    // would ever revisit.
    public async Task<IReadOnlyList<string>> GetChunkIdsForDocumentsAsync(IEnumerable<string> documentIds, CancellationToken ct = default)
    {
        const int pageSize = 1000;

        var idList = documentIds.ToList();
        if (idList.Count == 0) return [];

        var chunkIds = new List<string>();

        foreach (var batch in idList.Chunk(50))
        {
            var escaped   = batch.Select(id => id.Replace("'", "''"));
            var docFilter = $"search.in(document_id, '{string.Join(",", escaped)}', ',')";

            string? lastId = null;
            while (true)
            {
                var options = new SearchOptions
                {
                    Filter  = lastId is null
                        ? docFilter
                        : $"{docFilter} and id gt '{lastId.Replace("'", "''")}'",
                    Select  = { "id" },
                    OrderBy = { "id" },
                    Size    = pageSize,
                };

                var response  = await _client.SearchAsync<SearchDocument>("*", options, ct);
                var pageCount = 0;
                await foreach (var r in response.Value.GetResultsAsync().WithCancellation(ct))
                {
                    pageCount++;
                    if (r.Document.TryGetValue("id", out var idObj) && idObj is string chunkId)
                    {
                        chunkIds.Add(chunkId);
                        lastId = chunkId;
                    }
                }

                // A short page means we've reached the end - a full page means there may be more.
                if (pageCount < pageSize) break;
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
    public async Task<(long DocumentCount, long StorageSizeBytes, long? VectorIndexSizeBytes)> GetStatisticsAsync(CancellationToken ct = default)
    {
        var response = await _indexClient.GetIndexStatisticsAsync(_config.SearchIndexName, ct);
        // VectorIndexSize is the vector field and its HNSW graph together, and it is the half of
        // StorageSize that counts against the tier's own vector quota - the one that runs out
        // first. Nullable because the service has only reported it since the vector-quota
        // release: null is "not reported", never 0, or an index with no vectors and an index on
        // an older service version would read identically.
        return (response.Value.DocumentCount, response.Value.StorageSize, response.Value.VectorIndexSize);
    }
}
