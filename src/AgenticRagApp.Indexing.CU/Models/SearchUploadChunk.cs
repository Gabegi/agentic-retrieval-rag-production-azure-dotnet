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

    [property: JsonPropertyName("page_start")] int PageStart,
    [property: JsonPropertyName("page_end")] int PageEnd,

    [property: JsonPropertyName("char_count")] int CharCount,
    [property: JsonPropertyName("token_count")] int TokenCount,

    // Cleaned-text coordinates of this cut. Off the chunk itself, not the metadata - the
    // strategy set them when it decided where to cut.
    [property: JsonPropertyName("chunk_start")] int ChunkStart,
    [property: JsonPropertyName("chunk_length")] int ChunkLength,

    [property: JsonPropertyName("route_name")] string? RouteName,

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
    [property: JsonPropertyName("hyperlinks")] IReadOnlyList<string> Hyperlinks,
    [property: JsonPropertyName("annotations")] IReadOnlyList<string> Annotations,

    [property: JsonPropertyName("is_overlap")] bool IsOverlap,
    [property: JsonPropertyName("heading_located")] bool HeadingLocated,

    // ── Source-system facts (Zenya, 2026-09-21, D204 §9) ────────────────────
    // Fifteen fields off the blob's zenya_* metadata via DocumentStamp; see ChunkMetadata for
    // what each is and why it is kept apart from its look-alike. Trailing and defaulted so the
    // positional `new SearchUploadChunk(...)` in tests keeps compiling. The persons the chunk
    // carries are deliberately not projected (D204 §3d).
    [property: JsonPropertyName("source_document_id")]   string? SourceDocumentId   = null,
    [property: JsonPropertyName("source_version")]       string? SourceVersion      = null,
    [property: JsonPropertyName("source_revision")]      string? SourceRevision     = null,
    [property: JsonPropertyName("source_status")]        string? SourceStatus       = null,
    [property: JsonPropertyName("source_active")]        bool?   SourceActive       = null,
    [property: JsonPropertyName("source_title")]         string? SourceTitle        = null,
    [property: JsonPropertyName("source_language")]      string? SourceLanguage     = null,
    [property: JsonPropertyName("quick_code")]           string? QuickCode          = null,
    [property: JsonPropertyName("folder_path")]          string? FolderPath         = null,
    [property: JsonPropertyName("folder_name")]          string? FolderName         = null,
    [property: JsonPropertyName("source_type")]          string? SourceType         = null,
    [property: JsonPropertyName("source_document_type")] string? SourceDocumentType = null,
    [property: JsonPropertyName("summary")]              string? Summary            = null,
    [property: JsonPropertyName("check_date")]           DateTimeOffset? CheckDate  = null,
    [property: JsonPropertyName("attention_flags")]      IReadOnlyList<string>? AttentionFlags = null)
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
        PageStart:          chunk.Metadata.PageStart,
        PageEnd:            chunk.Metadata.PageEnd,
        CharCount:          chunk.CharCount,
        TokenCount:         chunk.Metadata.TokenCount,
        ChunkStart:         chunk.Start,
        ChunkLength:        chunk.Length,
        RouteName:          chunk.Metadata.Route,
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
        Hyperlinks:         chunk.Hyperlinks,
        Annotations:        chunk.Annotations,
        IsOverlap:          chunk.IsOverlap,
        HeadingLocated:     chunk.HeadingLocated,
        SourceDocumentId:   chunk.Metadata.SourceDocumentId,
        SourceVersion:      chunk.Metadata.SourceVersion,
        SourceRevision:     chunk.Metadata.SourceRevision,
        SourceStatus:       chunk.Metadata.SourceStatus,
        SourceActive:       chunk.Metadata.SourceActive,
        SourceTitle:        chunk.Metadata.SourceTitle,
        SourceLanguage:     chunk.Metadata.SourceLanguage,
        QuickCode:          chunk.Metadata.QuickCode,
        FolderPath:         chunk.Metadata.FolderPath,
        FolderName:         chunk.Metadata.FolderName,
        SourceType:         chunk.Metadata.SourceType,
        SourceDocumentType: chunk.Metadata.SourceDocumentType,
        Summary:            chunk.Metadata.Summary,
        CheckDate:          chunk.Metadata.CheckDate,
        AttentionFlags:     chunk.Metadata.AttentionFlags);
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
