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
    [property: JsonPropertyName("last_modified_datetime")] string? LastModifiedDateTime,

    // ── Added 2026-09-21 (D204 §2b) ─────────────────────────────────────────────────────────
    // The live tenant swagger showed this DTO carries ~38 fields; the 15 above were all we had
    // modelled, because D173 §1 was written against the public stand-in spec. Everything below
    // arrives on the SAME GET we already make once per document - no extra request, no extra
    // round trip. Trailing and defaulted on purpose: the record is constructed positionally in
    // ZenyaSyncServiceTests, and a field added after the fact must not break those call sites.
    //
    // Nothing here is a change signal. `version` remains the sync's only gate (ZenyaSyncService);
    // `revision` and `last_modified_datetime` are recorded, never compared - they are visible
    // only AFTER the per-document call they would have to gate, so gating on them is circular.
    // D204 §8 is the measurement that decides whether that ever changes.

    // The folder tree the document lives in. `full_path` is the curated, human-maintained
    // classification - the candidate to replace the D171 LLM domain-tag classifier, pending the
    // comparison D204 §5 asks for. Not acted on here; stamped so the comparison has data.
    [property: JsonPropertyName("folder_mini")] ZenyaFolderMini? Folder = null,

    // Author-written abstract. The one field on this DTO with no size bound - see
    // ZenyaBlobLayout's metadata budget, which drops it rather than failing the whole upload.
    [property: JsonPropertyName("summary")] string? Summary = null,

    // Review/validity, not modification: `check_date` is when the document is next due for
    // review, which is the real "is this policy still current" signal. Today valid_to is parsed
    // out of the TITLE by a regex (DocumentValidityParser); this is the fact that replaces it.
    [property: JsonPropertyName("check_date")] string? CheckDate = null,
    [property: JsonPropertyName("attention_required_flags")] IReadOnlyList<string>? AttentionRequiredFlags = null,
    [property: JsonPropertyName("can_check_document")] bool? CanCheckDocument = null,
    [property: JsonPropertyName("check_task_delegated_to_user")] ZenyaUserMini? CheckTaskDelegatedToUser = null,

    // Zenya's own authored language. The pipeline currently detects this per document via AI
    // Language (ExtractionService); this is the authoring fact.
    [property: JsonPropertyName("language")] string? Language = null,

    // What the document was before any download-time conversion; pairs with download_as_pdf to
    // answer D173's question 3 ("does /download return real PDF bytes") from metadata alone.
    [property: JsonPropertyName("original_type")] string? OriginalType = null,

    // The header Zenya renders on the document itself. Possibly carries title/code/version text
    // the extractor re-derives from the page; kept for that comparison, not used today.
    [property: JsonPropertyName("parsed_header")] string? ParsedHeader = null,
    [property: JsonPropertyName("unparsed_header")] string? UnparsedHeader = null,
    [property: JsonPropertyName("print_header_required")] bool? PrintHeaderRequired = null,

    // Person lists. Modelled and stamped because they ride this response for free, but D204 §3d
    // says they do NOT go to the search index: named individuals in a retrieval index is a
    // privacy decision for the PO, and the retrieval value is near zero. Dropping them later is
    // a one-line change in ZenyaBlobLayout, which is why they are kept separable here.
    [property: JsonPropertyName("authors")] IReadOnlyList<ZenyaUserMini>? Authors = null,
    [property: JsonPropertyName("authorizers")] IReadOnlyList<ZenyaUserMini>? Authorizers = null,
    [property: JsonPropertyName("document_administrators")] IReadOnlyList<ZenyaUserMini>? DocumentAdministrators = null,
    [property: JsonPropertyName("writers_group")] IReadOnlyList<ZenyaUserMini>? WritersGroup = null,
    [property: JsonPropertyName("invited_writers")] IReadOnlyList<ZenyaUserMini>? InvitedWriters = null,

    // Authoring/UI state. No retrieval value (D204 §3d) and no reader today; modelled so the
    // DTO matches the wire and so "we never fetched it" is never the reason something is absent.
    [property: JsonPropertyName("lock_info")] ZenyaLockInfo? LockInfo = null,
    [property: JsonPropertyName("marked_as_favorite")] bool? MarkedAsFavorite = null,
    [property: JsonPropertyName("is_printable")] bool? IsPrintable = null,
    [property: JsonPropertyName("is_editable_form")] bool? IsEditableForm = null,
    [property: JsonPropertyName("show_in_office_online_viewer")] bool? ShowInOfficeOnlineViewer = null,
    [property: JsonPropertyName("office_print_cover_page_mode")] string? OfficePrintCoverPageMode = null,
    [property: JsonPropertyName("has_delete_published_permission")] bool? HasDeletePublishedPermission = null,
    [property: JsonPropertyName("writer_hand_in_deadline_date")] string? WriterHandInDeadlineDate = null);

// document_type on the legacy document DTO (swagger: Infoland.Suite.Api.Controllers.Legacy.document_type_mini).
public sealed record ZenyaDocumentTypeMini(
    [property: JsonPropertyName("id")] int? Id,
    [property: JsonPropertyName("name")] string? Name);

// folder_mini on the legacy document DTO. `full_path` is the whole breadcrumb, `folder_name`
// only the leaf - both stamped, because which one makes a usable domain tag is exactly the
// open question (D204 §5) and guessing now would decide it silently.
public sealed record ZenyaFolderMini(
    [property: JsonPropertyName("folder_id")] int? FolderId,
    [property: JsonPropertyName("folder_name")] string? FolderName,
    [property: JsonPropertyName("full_path")] string? FullPath);

// The person shape on the legacy DTO's own lists: {user_id, user_name} only. The LISTING's
// involved_persons uses a richer shape (idp_user_id, and a non_user{name} variant for authors
// who are not Zenya users) - deliberately NOT modelled here, since that shape belongs to a call
// we do not yet make with include_involved_persons=true.
public sealed record ZenyaUserMini(
    [property: JsonPropertyName("user_id")] string? UserId,
    [property: JsonPropertyName("user_name")] string? UserName);

// lock_info on the legacy document DTO. Note locked_since_datetime is ISO-8601 here, not the
// yyyyMMddHHmmss the rest of the API uses - another reason every datetime on these records
// stays a string and the reader decides how to parse it.
public sealed record ZenyaLockInfo(
    [property: JsonPropertyName("locked")] bool? Locked,
    [property: JsonPropertyName("locked_since_datetime")] string? LockedSinceDateTime,
    [property: JsonPropertyName("locked_by_user")] ZenyaUserMini? LockedByUser);

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
