using System.Text.Json.Serialization;

namespace AgenticRagApp.Infrastructure.Clients.Zenya.Models;

// Wire shapes, v5, snake_case (D155 §1). Datetimes are Zenya's `yyyyMMddHHmmss` UTC strings and
// are kept as strings here: the sync (A5) decides what to parse, and a value it never reads
// should not be able to fail deserialisation. Null attributes are omitted by Zenya, so every
// non-key field is nullable. Unknown fields are ignored by System.Text.Json's default.

// GET /users/me. `login_code == "Anonymous"` is the failure signature - see ZenyaAnonymousException.
public sealed record ZenyaUser(
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("login_code")] string? LoginCode,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("user_type")] string? UserType,
    [property: JsonPropertyName("email_address")] string? EmailAddress)
{
    public const string AnonymousLoginCode = "Anonymous";
    public bool IsAnonymous => string.Equals(LoginCode, AnonymousLoginCode, StringComparison.Ordinal);
}

// GET /documents with envelope=true -> DocumentPagingDto (D173 §1). Without the envelope the
// response is a bare array and paging is guesswork, so the client always asks for it.
public sealed record ZenyaDocumentPage(
    [property: JsonPropertyName("data")] IReadOnlyList<ZenyaDocumentListItem> Data,
    [property: JsonPropertyName("pagination")] ZenyaPagination? Pagination);

public sealed record ZenyaPagination(
    [property: JsonPropertyName("limit")] int Limit,
    [property: JsonPropertyName("offset")] int Offset,
    [property: JsonPropertyName("returned")] int Returned,
    [property: JsonPropertyName("total")] int? Total);

// One row of the listing (DocumentGetDto). Deliberately thin - no mime type, document type or
// last-modified exist here (D173 §1); `version` is the only change signal, and the routing
// flags live on ZenyaDocumentMetadata, one GET per document away.
public sealed record ZenyaDocumentListItem(
    [property: JsonPropertyName("document_id")] string DocumentId,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("published_date_time")] string? PublishedDateTime,
    [property: JsonPropertyName("summary")] string? Summary);

// GET /documents/{id} - the legacy DTO, and the only place the routing discriminators exist
// (D173 §1, "Per-document metadata"). The sync routes on CanDownloadBinary / CanDownloadContent,
// never on Type alone.
public sealed record ZenyaDocumentMetadata(
    [property: JsonPropertyName("document_id")] string DocumentId,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("revision")] int? Revision,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("type")] string? Type,
    // An object, not a string: the first live payload (2026-09-11, D185 §5) rejected the string
    // model; the public swagger confirms `document_type_mini` = { id, name }. The blob metadata
    // carries the name.
    [property: JsonPropertyName("document_type")] ZenyaDocumentTypeMini? DocumentType,
    [property: JsonPropertyName("mime_type")] string? MimeType,
    [property: JsonPropertyName("download_binary_extension")] string? DownloadBinaryExtension,
    [property: JsonPropertyName("download_as_pdf")] bool? DownloadAsPdf,
    [property: JsonPropertyName("can_download_binary")] bool? CanDownloadBinary,
    [property: JsonPropertyName("can_download_content")] bool? CanDownloadContent,
    [property: JsonPropertyName("quick_code")] string? QuickCode,
    [property: JsonPropertyName("active")] bool? Active,
    // Lifecycle state string (spec: `state`, e.g. published) - what zenya_status records.
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("last_modified_datetime")] string? LastModifiedDateTime);

// document_type on the legacy document DTO (swagger: Infoland.Suite.Api.Controllers.Legacy.document_type_mini).
public sealed record ZenyaDocumentTypeMini(
    [property: JsonPropertyName("id")] int? Id,
    [property: JsonPropertyName("name")] string? Name);

// GET /documents/{id}/v{version}/contents - the typed route (D173 §1, "Content retrieval").
// `content` is a string whose shape (HTML, or JSON for modern_structured_document) is unknown
// until A8 measures it; kept opaque here on purpose (A9 is deliberately not designed yet).
public sealed record ZenyaDocumentContent(
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("schema_version")] string? SchemaVersion,
    [property: JsonPropertyName("viewer_script_url")] string? ViewerScriptUrl);

// RFC 7807 problem body Zenya returns on 4xx/5xx (D155 §5).
public sealed record ZenyaProblem(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("status")] int? Status,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("error_description")] string? ErrorDescription,
    [property: JsonPropertyName("error_code")] string? ErrorCode,
    [property: JsonPropertyName("error_date")] string? ErrorDate);
