using System.Text.Json.Serialization;

namespace AgenticRagApp.Indexing.CU.Models;

// Deliberately takes ChunkObject directly, unlike SnapshotChunk.From<T>, which is
// generic over ISnapshotSource. The snapshot is doc-type-agnostic by design (Observability
// must not reference a pipeline's chunk type), whereas this projection mirrors
// IndexService.BuildIndexDefinition field for field and is only meaningful for PDF chunks.
//
// The exact subset of ChunkObject that Azure AI Search's schema actually knows about -
// built right before the upload call (IndexDocumentService.UpsertDocumentsAsync), never
// persisted or passed between Durable activities itself. ChunkObject carries everything
// extraction produced (needed for the ChunkActivity -> EmbedAndUploadActivity blob
// hand-off and the Stage 2 archive) - uploading it directly would send fields Search has
// no schema for and rejects. Field set mirrors IndexService.BuildIndexDefinition exactly.
//
// Note the shape difference: ChunkObject splits the cut from the metadata, so half of these
// come off the chunk and half off chunk.Metadata. That is the projection's job.
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

    // Cleaned-text coordinates of this cut. Off the chunk itself, not the metadata - the
    // strategy set them when it decided where to cut.
    [property: JsonPropertyName("chunk_start")] int ChunkStart,
    [property: JsonPropertyName("chunk_length")] int ChunkLength,

    [property: JsonPropertyName("route_name")] string? RouteName,
    [property: JsonPropertyName("size_class")] string? SizeClass,

    [property: JsonPropertyName("valid_from")] DateTimeOffset? ValidFrom,
    [property: JsonPropertyName("valid_to")] DateTimeOffset? ValidTo,
    [property: JsonPropertyName("version")] string? Version,

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
    public static SearchUploadChunk From(ChunkObject chunk) => new(
        Id:                 chunk.Metadata.Id,
        DocumentId:         chunk.Metadata.DocumentId,
        SectionId:          chunk.Metadata.SectionId,
        SectionIndex:       chunk.SectionIndex,
        ChildIndex:         chunk.ChildIndex,
        Grain:              chunk.Metadata.Grain,
        Title:              chunk.Metadata.Title,
        Content:            chunk.Content,
        ParentText:         chunk.ParentText,
        HeadingText:        chunk.HeadingText,
        HeadingPath:        chunk.HeadingPath,
        HeadingDepth:       chunk.HeadingDepth,
        HeadingSource:      chunk.HeadingSource,
        LastModifiedDate:   chunk.Metadata.LastModifiedDate,
        CreatedAt:          chunk.Metadata.CreatedAt,
        ModDate:            chunk.Metadata.ModDate,
        PageCount:          chunk.Metadata.PageCount,
        ZenyaDocumentId:    chunk.Metadata.ZenyaDocumentId,
        ZenyaVersion:       chunk.Metadata.ZenyaVersion,
        ZenyaStatus:        chunk.Metadata.ZenyaStatus,
        ZenyaUrl:           chunk.Metadata.ZenyaUrl,
        PageStart:          chunk.Metadata.PageStart,
        PageEnd:            chunk.Metadata.PageEnd,
        CharCount:          chunk.CharCount,
        TokenCount:         chunk.Metadata.TokenCount,
        ChunkStart:         chunk.Start,
        ChunkLength:        chunk.Length,
        RouteName:          chunk.Metadata.Route,
        SizeClass:          chunk.Metadata.SizeClass,
        ValidFrom:          chunk.Metadata.ValidFrom,
        ValidTo:            chunk.Metadata.ValidTo,
        Version:            chunk.Metadata.Version,
        FamilyId:           chunk.Metadata.FamilyId,
        DomainTag:          chunk.Metadata.DomainTag,
        ConfusableWith:     chunk.Metadata.ConfusableWith,
        Population:         chunk.Metadata.Population,
        Language:           chunk.Metadata.Language,
        ContentVector:      chunk.ContentVector,
        TableCount:         chunk.TableCount,
        HasTable:           chunk.HasTable,
        FigureCaptions:     chunk.FigureCaptions,
        IsOverlap:          chunk.IsOverlap,
        HeadingLocated:     chunk.HeadingLocated,
        PageExtractionFlag: chunk.Metadata.PageExtractionFlag);
}

// The key plus one field, for patching family_id onto rows whose content did not change.
//
// A document is re-homed into a different family because OTHER documents changed the clustering.
// Its own bytes are untouched, so ExtractionService diffs it as skipped, it never reaches chunking
// and no ChunkObject for it exists this run - yet its indexed rows carry a family_id that is now
// wrong, in the field the knowledge agent filters on. So the fix patches the index directly from
// the chunk ids the index itself reports, and re-embeds nothing.
//
// DELIBERATELY NOT a partially-populated SearchUploadChunk: a merge writes every field the payload
// carries, so sending the 37-field projection with nulls in it would blank thirty-five columns on
// every row it touched. Two properties, both of them meant.
public record ChunkFamilyPatch(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("family_id")] string FamilyId);
