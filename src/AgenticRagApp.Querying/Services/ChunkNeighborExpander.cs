using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using AgenticRagApp.Querying.Models;

namespace AgenticRagApp.Querying.Services;

// Answers regularly continue on the next page of the same document ("op de volgende
// pagina staat hoe je een moreel beraad aanvraagt") — but only the page with query-term
// overlap gets retrieved by the knowledge base. For every hit, also fetch the chunks on
// the previous and next page of the same document via a raw search side-channel (the
// knowledge base retrieval client has no such primitive), then rebuild reading order
// per document.
public sealed class ChunkNeighborExpander
{
    private const int MaxContextChars = 16_000;   // keep the prompt bounded (~4k tokens)

    private readonly SearchClient _searchClient;

    public ChunkNeighborExpander(SearchClient searchClient) => _searchClient = searchClient;

    public async Task<IReadOnlyList<string>> ExpandAsync(IReadOnlyList<RetrievedChunk> hits, CancellationToken ct = default)
    {
        // Rank of each document = position of its best hit; preserves relevance order later.
        var docRank = new Dictionary<string, int>();
        for (int i = 0; i < hits.Count; i++)
            if (!string.IsNullOrEmpty(hits[i].DocumentId) && !docRank.ContainsKey(hits[i].DocumentId))
                docRank[hits[i].DocumentId] = i;

        var seenIds  = hits.Select(h => h.Id).ToHashSet();
        var expanded = new List<RetrievedChunk>(hits);

        var groups = hits.GroupBy(h => h.DocumentId).Where(g => !string.IsNullOrEmpty(g.Key)).ToList();
        var neighborResults = await Task.WhenAll(groups.Select(g => FetchNeighborsAsync(g, ct)));

        foreach (var neighbors in neighborResults)
            foreach (var chunk in neighbors)
                if (seenIds.Add(chunk.Id))
                    expanded.Add(chunk);

        // Documents in relevance order, chunks within a document in reading order,
        // total context capped so neighbor expansion can't blow up the prompt.
        var ordered = expanded
            .GroupBy(c => c.DocumentId)
            .OrderBy(g => docRank.TryGetValue(g.Key, out var r) ? r : int.MaxValue)
            .SelectMany(g => g.OrderBy(c => c.Page).ThenBy(c => c.ChunkIndex));

        var chunks = new List<string>();
        int total  = 0;
        foreach (var chunk in ordered)
        {
            var text = chunk.ToContextText();
            if (total + text.Length > MaxContextChars)
                break;
            chunks.Add(text);
            total += text.Length;
        }
        return chunks;
    }

    private async Task<List<RetrievedChunk>> FetchNeighborsAsync(IGrouping<string, RetrievedChunk> group, CancellationToken ct)
    {
        var result = new List<RetrievedChunk>();

        var pages  = group.Select(h => h.Page).ToHashSet();
        var wanted = pages.SelectMany(p => new[] { p - 1, p + 1 })
                          .Where(p => p >= 0 && !pages.Contains(p))
                          .Distinct()
                          .ToList();
        if (wanted.Count == 0)
            return result;

        var docId  = group.Key.Replace("'", "''");   // OData string-literal escaping
        // page_start/child_index replace page_number/chunk_index (action-plan.md §4.6).
        // page_start is now IsFilterable - it previously was not, so this filter could only
        // ever have failed with a 400 against the old schema.
        var filter = $"document_id eq '{docId}' and " +
                     $"({string.Join(" or ", wanted.Select(p => $"page_start eq {p}"))})";

        var neighborOpts = new SearchOptions
        {
            Filter = filter,
            Select = { "id", "document_id", "content", "page_start", "child_index" },
            Size   = wanted.Count * 4,   // pages are 1-2 chunks each; headroom is cheap
        };

        var response = await _searchClient.SearchAsync<SearchDocument>("*", neighborOpts, ct);
        await foreach (var hit in response.Value.GetResultsAsync())
        {
            var content = hit.Document.GetString("content") ?? "";
            if (string.IsNullOrWhiteSpace(content))
                continue;
            result.Add(new RetrievedChunk(
                Id:         hit.Document.GetString("id") ?? "",
                DocumentId: group.Key,
                Page:       hit.Document.GetInt32("page_start") ?? 0,
                ChunkIndex: hit.Document.GetInt32("child_index") ?? 0,
                Title:      null,
                Summary:    null,   // already surfaced once on the original matched chunk for this document
                Content:    content));
        }
        return result;
    }
}
