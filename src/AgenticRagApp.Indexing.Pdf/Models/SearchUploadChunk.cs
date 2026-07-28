using System.Text.Json.Serialization;

namespace AgenticRagApp.Indexing.Pdf.Models;

// The exact subset of DocumentChunk that Azure AI Search's schema actually knows about -
// built right before the upload call (IndexDocumentService.UpsertDocumentsAsync), never
// persisted or passed between Durable activities itself. DocumentChunk carries everything
// extraction produced (needed for the ChunkActivity -> EmbedAndUploadActivity blob
// hand-off and the Stage 2 archive) - uploading it directly would send fields Search has
// no schema for and rejects. Field set mirrors IndexService.BuildIndexDefinition exactly.
public record SearchUploadChunk(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("document_id")] string DocumentId,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("last_modified_date")] DateTimeOffset? LastModifiedDate,
    [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt,
    [property: JsonPropertyName("mod_date")] DateTimeOffset? ModDate,
    [property: JsonPropertyName("page_count")] int? PageCount,
    [property: JsonPropertyName("zenya_document_id")] string? ZenyaDocumentId,
    [property: JsonPropertyName("zenya_version")] string? ZenyaVersion,
    [property: JsonPropertyName("zenya_status")] string? ZenyaStatus,
    [property: JsonPropertyName("zenya_url")] string? ZenyaUrl,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("heading")] string? Heading,
    [property: JsonPropertyName("page_number")] int PageNumber,
    [property: JsonPropertyName("chunk_index")] int ChunkIndex,
    [property: JsonPropertyName("content_vector")] float[]? ContentVector,
    [property: JsonPropertyName("table_count")] int TableCount,
    [property: JsonPropertyName("has_table")] bool HasTable,
    [property: JsonPropertyName("figure_captions")] IReadOnlyList<string> FigureCaptions)
{
    public static SearchUploadChunk From(DocumentChunk doc) => new(
        Id:               doc.Id,
        DocumentId:       doc.DocumentId,
        Title:            doc.Title,
        LastModifiedDate: doc.LastModifiedDate,
        CreatedAt:        doc.CreatedAt,
        ModDate:          doc.ModDate,
        PageCount:        doc.PageCount,
        ZenyaDocumentId:  doc.ZenyaDocumentId,
        ZenyaVersion:     doc.ZenyaVersion,
        ZenyaStatus:      doc.ZenyaStatus,
        ZenyaUrl:         doc.ZenyaUrl,
        Content:          doc.Content,
        Heading:          doc.Heading,
        PageNumber:       doc.PageNumber,
        ChunkIndex:       doc.ChunkIndex,
        ContentVector:    doc.ContentVector,
        TableCount:       doc.TableCount,
        HasTable:         doc.HasTable,
        FigureCaptions:   doc.FigureCaptions);
}
