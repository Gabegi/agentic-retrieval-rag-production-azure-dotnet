using System.Text.Json.Serialization;

namespace AgenticRagApp.Indexing.Pdf.Models;

// Deliberately takes DocumentChunk directly, unlike SnapshotChunk.From<T>, which is
// generic over ISnapshotSource. The snapshot is doc-type-agnostic by design (Observability
// must not reference a pipeline's chunk type), whereas this projection mirrors
// IndexService.BuildIndexDefinition field for field and is only meaningful for PDF chunks.
//
// The exact subset of DocumentChunk that Azure AI Search's schema actually knows about -
// built right before the upload call (IndexDocumentService.UpsertDocumentsAsync), never
// persisted or passed between Durable activities itself. DocumentChunk carries everything
// extraction produced (needed for the ChunkActivity -> EmbedAndUploadActivity blob
// hand-off and the Stage 2 archive) - uploading it directly would send fields Search has
// no schema for and rejects. Field set mirrors IndexService.BuildIndexDefinition exactly.
//
// The CSV-era fields (summary, department, quick_code, relative_path, check_date, version)
// are gone: the CSV pipeline is not wired into the FunctionApp at all - no trigger, no DI
// registration - and every one of those fields was documented "Null for PDF rows". PDF and
// CSV share nothing here now (action-plan.md B2).
public record SearchUploadChunk(
    // ── Identity and position (action-plan.md §4.6) ─────────────────────────
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("document_id")] string DocumentId,
    [property: JsonPropertyName("section_id")] string? SectionId,
    [property: JsonPropertyName("section_index")] int SectionIndex,
    [property: JsonPropertyName("child_index")] int ChildIndex,
    [property: JsonPropertyName("grain")] string Grain,

    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("parent_text")] string? ParentText,
    [property: JsonPropertyName("heading_text")] string? HeadingText,
    [property: JsonPropertyName("heading_path")] string? HeadingPath,
    [property: JsonPropertyName("heading_depth")] int HeadingDepth,
    [property: JsonPropertyName("heading_source")] string? HeadingSource,

    [property: JsonPropertyName("last_modified_date")] DateTimeOffset? LastModifiedDate,
    [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt,
    [property: JsonPropertyName("mod_date")] DateTimeOffset? ModDate,
    [property: JsonPropertyName("page_count")] int? PageCount,
    [property: JsonPropertyName("zenya_document_id")] string? ZenyaDocumentId,
    [property: JsonPropertyName("zenya_version")] string? ZenyaVersion,
    [property: JsonPropertyName("zenya_status")] string? ZenyaStatus,
    [property: JsonPropertyName("zenya_url")] string? ZenyaUrl,

    [property: JsonPropertyName("page_start")] int PageStart,
    [property: JsonPropertyName("page_end")] int PageEnd,

    [property: JsonPropertyName("char_count")] int CharCount,
    [property: JsonPropertyName("token_count")] int TokenCount,

    [property: JsonPropertyName("family_id")] string? FamilyId,
    [property: JsonPropertyName("domain_tag")] string? DomainTag,
    [property: JsonPropertyName("confusable_with")] IReadOnlyList<string> ConfusableWith,
    [property: JsonPropertyName("population")] string? Population,
    [property: JsonPropertyName("language")] string? Language,

    [property: JsonPropertyName("content_vector")] float[]? ContentVector,
    [property: JsonPropertyName("table_count")] int TableCount,
    [property: JsonPropertyName("has_table")] bool HasTable,
    [property: JsonPropertyName("figure_captions")] IReadOnlyList<string> FigureCaptions,

    [property: JsonPropertyName("is_overlap")] bool IsOverlap,
    [property: JsonPropertyName("heading_located")] bool HeadingLocated,
    [property: JsonPropertyName("page_extraction_flag")] bool PageExtractionFlag)
{
    public static SearchUploadChunk From(DocumentChunk doc) => new(
        Id:                 doc.Id,
        DocumentId:         doc.DocumentId,
        SectionId:          doc.SectionId,
        SectionIndex:       doc.SectionIndex,
        ChildIndex:         doc.ChildIndex,
        Grain:              doc.Grain,
        Title:              doc.Title,
        Content:            doc.Content,
        ParentText:         doc.ParentText,
        HeadingText:        doc.HeadingText,
        HeadingPath:        doc.HeadingPath,
        HeadingDepth:       doc.HeadingDepth,
        HeadingSource:      doc.HeadingSource,
        LastModifiedDate:   doc.LastModifiedDate,
        CreatedAt:          doc.CreatedAt,
        ModDate:            doc.ModDate,
        PageCount:          doc.PageCount,
        ZenyaDocumentId:    doc.ZenyaDocumentId,
        ZenyaVersion:       doc.ZenyaVersion,
        ZenyaStatus:        doc.ZenyaStatus,
        ZenyaUrl:           doc.ZenyaUrl,
        PageStart:          doc.PageStart,
        PageEnd:            doc.PageEnd,
        CharCount:          doc.CharCount,
        TokenCount:         doc.TokenCount,
        FamilyId:           doc.FamilyId,
        DomainTag:          doc.DomainTag,
        ConfusableWith:     doc.ConfusableWith,
        Population:         doc.Population,
        Language:           doc.Language,
        ContentVector:      doc.ContentVector,
        TableCount:         doc.TableCount,
        HasTable:           doc.HasTable,
        FigureCaptions:     doc.FigureCaptions,
        IsOverlap:          doc.IsOverlap,
        HeadingLocated:     doc.HeadingLocated,
        PageExtractionFlag: doc.PageExtractionFlag);
}
