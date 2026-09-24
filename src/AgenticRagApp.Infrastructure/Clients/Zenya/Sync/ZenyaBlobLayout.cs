using System.Text;
using AgenticRagApp.Infrastructure.Clients.Zenya.Models;

namespace AgenticRagApp.Infrastructure.Clients.Zenya.Sync;

// The on-disk contract of the zenya-documents container: where a document lands and what its
// blob metadata says. Everything the indexing side will later read off this container is
// defined here and nowhere else (D185).
//
// Layout - two virtual folders (blob-name prefixes), chosen 2026-09-11:
//   pdf/{document_id}.pdf     what /download returned as a PDF
//   docs/{document_id}.{ext}  every other binary (Word mostly), real extension, ".bin" if unknown
// The blob name doubles as the indexer's SourceId (PdfExtractionDocument: SourceId == blobName),
// so it is the Zenya document_id, never the title: a rename in Zenya must not look like a new
// document (D175 A5). When the PDF indexer is pointed at this container it lists the pdf/
// prefix only, so Word files never reach Content Understanding by accident.
public static class ZenyaBlobLayout
{
    public const string PdfPrefix  = "pdf/";
    public const string DocsPrefix = "docs/";

    // The harvest sidecars (D243 Part 2). meta/{document_id}.json holds every raw answer Zenya
    // gave about that document; _tenant/{runId}.json holds what is true of the tenant rather
    // than of one document. Both carry zenya_document_id + zenya_version metadata so the sync's
    // own listing recognises them (not foreign) and the removal pass deletes a removed document's
    // sidecar with its binary. The indexer never sees them: IndexDiffService keeps only *.pdf.
    public const string MetaPrefix   = "meta/";
    public const string TenantPrefix = "_tenant/";

    public static string MetaBlobNameFor(string documentId) => $"{MetaPrefix}{documentId}.json";
    public static string TenantBlobNameFor(DateTimeOffset runStartedUtc) =>
        $"{TenantPrefix}{runStartedUtc.ToUniversalTime():yyyyMMddTHHmmssZ}.json";

    public static bool IsSidecar(string blobName) =>
        blobName.StartsWith(MetaPrefix, StringComparison.Ordinal) || blobName.StartsWith(TenantPrefix, StringComparison.Ordinal);

    // Blob metadata keys. Free-text values (title, quick code, type names) are ALWAYS
    // RFC 3986 percent-encoded (Uri.EscapeDataString): blob metadata values must be ASCII and
    // Dutch titles are not. Readers decode with Uri.UnescapeDataString. Identifiers, numbers,
    // Zenya's yyyyMMddHHmmss timestamps and mime types are ASCII by construction and stored raw.
    public const string DocumentIdKey   = "zenya_document_id";   // raw GUID - the removal pass keys on this
    public const string VersionKey      = "zenya_version";       // raw int - the change signal
    public const string StatusKey       = "zenya_status";        // raw; the DTO `state`, else the listing state
    public const string QuickCodeKey    = "zenya_quick_code";    // encoded (Snelcode)
    public const string TitleKey        = "zenya_title";         // encoded
    public const string TypeKey         = "zenya_type";          // encoded
    public const string DocumentTypeKey = "zenya_document_type"; // encoded
    public const string MimeTypeKey     = "zenya_mime_type";     // raw
    public const string LastModifiedKey = "zenya_last_modified"; // raw, Zenya's yyyyMMddHHmmss UTC string
    public const string SyncedAtKey     = "zenya_synced_at";     // raw, ISO-8601 UTC of this write

    // Added 2026-09-21 (D204). Everything below already rode the per-document GET the sync makes;
    // it was simply not modelled. No new request, no new call - see ZenyaDocumentMetadata.
    // The indexer reads none of these yet: that is the container-switch step (D202 §6), which is
    // also the one index recreate they should all ride on.
    public const string FolderPathKey   = "zenya_folder_path";   // encoded, folder_mini.full_path
    public const string FolderNameKey   = "zenya_folder_name";   // encoded, folder_mini.folder_name (leaf only)
    public const string FolderIdKey     = "zenya_folder_id";     // raw int
    public const string SummaryKey      = "zenya_summary";       // encoded - the one unbounded value, see the budget below
    public const string CheckDateKey    = "zenya_check_date";    // raw; review due date, the candidate for valid_to
    public const string AttentionKey    = "zenya_attention_flags";        // raw, comma-joined enum names
    public const string CanCheckKey     = "zenya_can_check_document";     // raw bool
    public const string CheckDelegateKey= "zenya_check_delegated_to";     // encoded user_name
    public const string LanguageKey     = "zenya_language";      // raw
    public const string OriginalTypeKey = "zenya_original_type"; // encoded
    public const string RevisionKey     = "zenya_revision";      // raw int - RECORDED, never compared (D204 §8)
    public const string ActiveKey       = "zenya_active";        // raw bool - the authoritative liveness flag
    public const string DownloadAsPdfKey= "zenya_download_as_pdf";        // raw bool
    public const string ParsedHeaderKey = "zenya_parsed_header";          // encoded
    public const string UnparsedHeaderKey = "zenya_unparsed_header";      // encoded
    public const string PrintHeaderKey  = "zenya_print_header_required";  // raw bool
    public const string AuthorsKey      = "zenya_authors";                // encoded, "; "-joined user_name
    public const string AuthorizersKey  = "zenya_authorizers";            // encoded, joined
    public const string AdministratorsKey = "zenya_document_administrators"; // encoded, joined
    public const string WritersGroupKey = "zenya_writers_group";          // encoded, joined
    public const string InvitedWritersKey = "zenya_invited_writers";      // encoded, joined
    public const string LockedKey       = "zenya_locked";                 // raw bool
    public const string LockedSinceKey  = "zenya_locked_since";           // raw, ISO-8601 here (not yyyyMMddHHmmss)
    public const string LockedByKey     = "zenya_locked_by";              // encoded user_name
    public const string FavoriteKey     = "zenya_marked_as_favorite";     // raw bool
    public const string PrintableKey    = "zenya_is_printable";           // raw bool
    public const string EditableFormKey = "zenya_is_editable_form";       // raw bool
    public const string OfficeViewerKey = "zenya_show_in_office_online_viewer"; // raw bool
    public const string CoverPageKey    = "zenya_office_print_cover_page_mode"; // encoded
    public const string CanDeleteKey    = "zenya_has_delete_published_permission"; // raw bool
    public const string HandInDeadlineKey = "zenya_writer_hand_in_deadline";      // raw

    // Fallback for zenya_status when the document DTO carries no `state`: the listing state the
    // sync asks for (Zenya default; which states the corpus needs is an open product question,
    // D175). With `state` present, that documented value is recorded instead.
    public const string PublishedStatus = "published";

    // Azure caps a blob's metadata at 8 KiB total, counting names and values on the wire; an
    // upload that exceeds it fails with 400 and the sync loses the whole document over a metadata
    // byte. The budget below is deliberately under 8 KiB because the exact wire cost (the
    // "x-ms-meta-" prefix per entry, header framing) is not something this method can measure -
    // SizeOf models it and errs high.
    public const int MetadataByteBudget = 7_600;
    private const string WireKeyPrefix = "x-ms-meta-";

    // Dropped in THIS order when the budget is exceeded - declared, not computed, so the same
    // document always loses the same key. Headers first (the extractor re-derives them from the
    // page anyway), then the person lists (D204 §3d: not going to the index regardless), and
    // summary last, because it is the only one of the group with retrieval value.
    // Everything not listed here is never dropped: identity, version, lifecycle and the fields
    // the diff will key on have to be present or the blob is not interpretable.
    private static readonly string[] DroppableInOrder =
    [
        UnparsedHeaderKey, ParsedHeaderKey,
        InvitedWritersKey, WritersGroupKey, AdministratorsKey, AuthorizersKey, AuthorsKey,
        SummaryKey,
    ];

    // onDropped is called once per key removed to fit the budget. A drop is silent data loss
    // otherwise, and "the field was never fetched" must never be indistinguishable from "it did
    // not fit" - the caller logs it.
    public static IReadOnlyDictionary<string, string> BuildMetadata(
        ZenyaDocumentMetadata document, string? mimeType, DateTimeOffset syncedAt,
        Action<string>? onDropped = null)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DocumentIdKey] = document.DocumentId,
            [VersionKey]    = document.Version.ToString(),
            [StatusKey]     = string.IsNullOrWhiteSpace(document.State) ? PublishedStatus : document.State.Trim(),
            [SyncedAtKey]   = syncedAt.ToUniversalTime().ToString("O"),
        };
        AddEncoded(metadata, QuickCodeKey,    document.QuickCode);
        AddEncoded(metadata, TitleKey,        document.Title);
        AddEncoded(metadata, TypeKey,         document.Type);
        AddEncoded(metadata, DocumentTypeKey, document.DocumentType?.Name);
        AddRaw(metadata, MimeTypeKey,     mimeType);
        AddRaw(metadata, LastModifiedKey, document.LastModifiedDateTime);

        // ── D204 fields, from the same GET ──────────────────────────────────────────────────
        AddEncoded(metadata, FolderPathKey,   document.Folder?.FullPath);
        AddEncoded(metadata, FolderNameKey,   document.Folder?.FolderName);
        AddRaw(metadata, FolderIdKey,         document.Folder?.FolderId?.ToString());
        AddEncoded(metadata, SummaryKey,      document.Summary);
        AddRaw(metadata, CheckDateKey,        document.CheckDate);
        AddRaw(metadata, AttentionKey,        Join(document.AttentionRequiredFlags, ","));
        AddRaw(metadata, CanCheckKey,         Bool(document.CanCheckDocument));
        AddEncoded(metadata, CheckDelegateKey, document.CheckTaskDelegatedToUser?.UserName);
        AddRaw(metadata, LanguageKey,         document.Language);
        AddEncoded(metadata, OriginalTypeKey, document.OriginalType);
        AddRaw(metadata, RevisionKey,         document.Revision?.ToString());
        AddRaw(metadata, ActiveKey,           Bool(document.Active));
        AddRaw(metadata, DownloadAsPdfKey,    Bool(document.DownloadAsPdf));
        AddEncoded(metadata, ParsedHeaderKey,   document.ParsedHeader);
        AddEncoded(metadata, UnparsedHeaderKey, document.UnparsedHeader);
        AddRaw(metadata, PrintHeaderKey,      Bool(document.PrintHeaderRequired));
        AddEncoded(metadata, AuthorsKey,        Names(document.Authors));
        AddEncoded(metadata, AuthorizersKey,    Names(document.Authorizers));
        AddEncoded(metadata, AdministratorsKey, Names(document.DocumentAdministrators));
        AddEncoded(metadata, WritersGroupKey,   Names(document.WritersGroup));
        AddEncoded(metadata, InvitedWritersKey, Names(document.InvitedWriters));
        AddRaw(metadata, LockedKey,           Bool(document.LockInfo?.Locked));
        AddRaw(metadata, LockedSinceKey,      document.LockInfo?.LockedSinceDateTime);
        AddEncoded(metadata, LockedByKey,     document.LockInfo?.LockedByUser?.UserName);
        AddRaw(metadata, FavoriteKey,         Bool(document.MarkedAsFavorite));
        AddRaw(metadata, PrintableKey,        Bool(document.IsPrintable));
        AddRaw(metadata, EditableFormKey,     Bool(document.IsEditableForm));
        AddRaw(metadata, OfficeViewerKey,     Bool(document.ShowInOfficeOnlineViewer));
        AddEncoded(metadata, CoverPageKey,    document.OfficePrintCoverPageMode);
        AddRaw(metadata, CanDeleteKey,        Bool(document.HasDeletePublishedPermission));
        AddRaw(metadata, HandInDeadlineKey,   document.WriterHandInDeadlineDate);

        FitToBudget(metadata, onDropped);
        return metadata;
    }

    // Removes droppable keys, in the declared order, until the metadata fits. Stops as soon as it
    // does - the aim is a successful upload carrying as much as will fit, not a minimal one.
    private static void FitToBudget(Dictionary<string, string> metadata, Action<string>? onDropped)
    {
        if (SizeOf(metadata) <= MetadataByteBudget) return;
        foreach (var key in DroppableInOrder)
        {
            if (!metadata.Remove(key)) continue;
            onDropped?.Invoke(key);
            if (SizeOf(metadata) <= MetadataByteBudget) return;
        }
    }

    // Values are ASCII by construction here (encoded or checked in AddRaw), so one char is one
    // byte; the prefix is counted per entry because that is what goes on the wire.
    private static int SizeOf(Dictionary<string, string> metadata) =>
        metadata.Sum(kv => WireKeyPrefix.Length + kv.Key.Length + kv.Value.Length);

    private static string? Bool(bool? value) => value is null ? null : value.Value ? "true" : "false";

    private static string? Names(IReadOnlyList<ZenyaUserMini>? users) =>
        Join(users?.Select(u => u.UserName).Where(n => !string.IsNullOrWhiteSpace(n)).ToList(), "; ");

    private static string? Join(IReadOnlyList<string?>? values, string separator) =>
        values is { Count: > 0 } ? string.Join(separator, values) : null;

    // Name from what the download actually was, in this order: the response's Content-Type, the
    // metadata's download_binary_extension, the metadata's mime_type. The response wins because
    // Zenya can render some documents as PDF on download (download_as_pdf) regardless of the
    // stored type - D173 §"first full run" asks exactly this question, and naming by the answer
    // keeps the container truthful.
    public static string BlobNameFor(ZenyaDocumentMetadata document, string? responseContentType)
    {
        var extension = ExtensionFor(responseContentType, document.DownloadBinaryExtension, document.MimeType);
        var prefix = extension == "pdf" ? PdfPrefix : DocsPrefix;
        return $"{prefix}{document.DocumentId}.{extension}";
    }

    public static string ExtensionFor(string? responseContentType, string? declaredExtension, string? declaredMimeType)
    {
        if (ExtensionForMime(responseContentType) is { } fromResponse) return fromResponse;
        if (!string.IsNullOrWhiteSpace(declaredExtension))
        {
            var ext = declaredExtension.Trim().TrimStart('.').ToLowerInvariant();
            if (ext.Length > 0 && ext.All(c => char.IsAsciiLetterOrDigit(c))) return ext;
        }
        return ExtensionForMime(declaredMimeType) ?? "bin";
    }

    // Only the types a document-management system plausibly serves; anything else is "bin" with
    // the mime type preserved in metadata, so nothing is guessed.
    private static string? ExtensionForMime(string? mime)
    {
        if (string.IsNullOrWhiteSpace(mime)) return null;
        var media = mime.Split(';')[0].Trim().ToLowerInvariant();
        return media switch
        {
            "application/pdf" => "pdf",
            "application/msword" => "doc",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => "docx",
            "application/vnd.ms-excel" => "xls",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => "xlsx",
            "application/vnd.ms-powerpoint" => "ppt",
            "application/vnd.openxmlformats-officedocument.presentationml.presentation" => "pptx",
            "text/plain" => "txt",
            "text/html" => "html",
            _ => null,
        };
    }

    // "%PDF" at offset 0 (PDF 1.7 §7.5.2). Reported per run so the first full sync answers D173's
    // question 3 ("does /download return real PDF bytes") with a count, not an assumption.
    public static bool HasPdfMagic(ReadOnlySpan<byte> head) =>
        head.Length >= 4 && head[0] == (byte)'%' && head[1] == (byte)'P' && head[2] == (byte)'D' && head[3] == (byte)'F';

    private static void AddEncoded(Dictionary<string, string> metadata, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) metadata[key] = Uri.EscapeDataString(value.Trim());
    }

    private static void AddRaw(Dictionary<string, string> metadata, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var trimmed = value.Trim();
        // Defensive: a raw field that turns out non-ASCII would fail the whole upload with a 400;
        // encode it rather than lose the document over a metadata byte.
        metadata[key] = Encoding.ASCII.GetByteCount(trimmed) == trimmed.Length && !trimmed.Any(char.IsControl)
            ? trimmed
            : Uri.EscapeDataString(trimmed);
    }
}
