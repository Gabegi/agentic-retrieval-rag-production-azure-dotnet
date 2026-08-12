using Azure.Search.Documents.KnowledgeBases.Models;
using AgenticRagApp.Querying.Models;

namespace AgenticRagApp.Querying.Services;

// Converts knowledge-base references into RetrievedChunk records the rest of the
// querying pipeline works with. SourceData values are BinaryData (raw JSON), so
// they're deserialized rather than ToString()'d, which would leave string fields
// JSON-quoted/escaped.
public static class KnowledgeBaseReferenceMapper
{
    public static IReadOnlyList<RetrievedChunk> Map(IEnumerable<KnowledgeBaseReference> references)
    {
        var chunks = new List<RetrievedChunk>();
        foreach (var r in references)
        {
            if (r.SourceData is null || !r.SourceData.TryGetValue("content", out var contentRaw))
                continue;
            var content = AsText(contentRaw);
            if (string.IsNullOrWhiteSpace(content))
                continue;

            r.SourceData.TryGetValue("id", out var idRaw);
            r.SourceData.TryGetValue("document_id", out var docIdRaw);
            r.SourceData.TryGetValue("title", out var titleRaw);
            // page_start/child_index replace page_number/chunk_index (action-plan.md §4.6).
            // summary/quick_code/relative_path were CSV-only and are gone from the schema -
            // PDF and CSV no longer share an index (B2), so they were always null here.
            r.SourceData.TryGetValue("page_start", out var pageRaw);
            r.SourceData.TryGetValue("child_index", out var chunkIndexRaw);
            r.SourceData.TryGetValue("zenya_document_id", out var zenyaDocIdRaw);
            r.SourceData.TryGetValue("zenya_version", out var zenyaVersionRaw);
            r.SourceData.TryGetValue("zenya_status", out var zenyaStatusRaw);
            r.SourceData.TryGetValue("zenya_url", out var zenyaUrlRaw);
            r.SourceData.TryGetValue("page_count", out var pageCountRaw);
            r.SourceData.TryGetValue("created_at", out var createdAtRaw);
            r.SourceData.TryGetValue("mod_date", out var modDateRaw);

            chunks.Add(new RetrievedChunk(
                Id:              AsText(idRaw) ?? "",
                DocumentId:      AsText(docIdRaw) ?? "",
                Page:            AsInt(pageRaw),
                ChunkIndex:      AsInt(chunkIndexRaw),
                Title:           AsText(titleRaw),
                Summary:         null,
                Content:         content,
                QuickCode:       null,
                RelativePath:    null,
                ZenyaDocumentId: AsText(zenyaDocIdRaw),
                ZenyaVersion:    AsText(zenyaVersionRaw),
                ZenyaStatus:     AsText(zenyaStatusRaw),
                ZenyaUrl:        AsText(zenyaUrlRaw),
                PageCount:       AsNullableInt(pageCountRaw),
                CreatedAt:       AsDateTimeOffset(createdAtRaw),
                ModDate:         AsDateTimeOffset(modDateRaw)));
        }
        return chunks;
    }

    private static string? AsText(object? value) => value switch
    {
        null => null,
        BinaryData bd => bd.ToObjectFromJson<string>(),
        _ => value.ToString(),
    };

    private static int AsInt(object? value) => value switch
    {
        null => 0,
        BinaryData bd => bd.ToObjectFromJson<int>(),
        _ => Convert.ToInt32(value),
    };

    private static int? AsNullableInt(object? value) => value switch
    {
        null => null,
        BinaryData bd => bd.ToObjectFromJson<int?>(),
        _ => Convert.ToInt32(value),
    };

    // created_at/mod_date are absent (null, not 0) as often as they're set - unlike
    // page_number/chunk_index (AsInt above), a 0-default here would read as a real,
    // wildly-wrong date rather than "unknown".
    private static DateTimeOffset? AsDateTimeOffset(object? value) => value switch
    {
        null => null,
        BinaryData bd => bd.ToObjectFromJson<DateTimeOffset?>(),
        _ => DateTimeOffset.Parse(value.ToString()!),
    };
}
