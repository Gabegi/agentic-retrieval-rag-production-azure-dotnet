using System.Globalization;
using AgenticRagApp.Infrastructure.Clients.Zenya.Sync;

namespace AgenticRagApp.Indexing.CU.Models;

// Zenya's own facts about a document, read off blob metadata - never present in the PDF's own
// bytes (confirmed empirically: Zenya's Documentgegevens fields - Snelcode, Versie, etc. - don't
// appear anywhere in a sample file's Info dictionary, content streams, or XMP packet).
//
// This is the READ side of one contract whose WRITE side is ZenyaBlobLayout (Infrastructure):
// the ZenyaSync tool stamps every blob it writes into the zenya-documents container, and this
// record decodes exactly those keys - by ZenyaBlobLayout's constants, so a renamed key cannot
// silently split the two. Free-text values arrive RFC 3986 percent-encoded (blob metadata must be
// ASCII; Dutch titles are not) and are decoded here; identifiers, numbers, booleans and Zenya's
// yyyyMMdd / yyyyMMddHHmmss timestamps arrive raw.
//
// Until 2026-09-21 this read four keys nothing set (D094). It now reads everything the sync
// writes (D204 §9) and rides on PdfBlobInfo -> PdfExtractionDocument -> DocumentStamp, where the
// document-level subset is stamped onto every chunk. On the manually uploaded "protocols"
// corpus no blob carries any of these keys, so every property is null there - absent, not
// "known to be none" - and the pipeline behaves exactly as before.
//
// Nothing here is a change signal for the indexer. The diff still keys on blob LastModified
// (IndexDiffService); moving it to Version is D202's design, not built.
public sealed record ZenyaMetadata
{
    public static readonly ZenyaMetadata Empty = new();

    // ── Identity and lifecycle ─────────────────────────────────────────────────────────────
    public string?  DocumentId { get; init; }   // Zenya GUID; the blob NAME is the SourceId, this is the source's own id
    public int?     Version    { get; init; }   // the sync's change signal
    public int?     Revision   { get; init; }   // minor edit within a version - recorded, never compared (D204 §8)
    public string?  Status     { get; init; }   // Zenya `state`, else the listing state ("published")
    public bool?    Active     { get; init; }   // the authoritative liveness flag
    public string?  Url        { get; init; }   // never written by the sync (no deep-link pattern, D094); kept for a hand-set value

    // ── Classification ─────────────────────────────────────────────────────────────────────
    public string?  QuickCode    { get; init; } // Snelcode, e.g. "NW-46-42" - what staff type
    public string?  Title        { get; init; }
    public string?  Summary      { get; init; }
    public string?  Type         { get; init; } // file | structured_document | ... (Zenya `type`)
    public string?  DocumentType { get; init; } // document_type.name, e.g. "Protocol"
    public string?  MimeType     { get; init; }
    public string?  OriginalType { get; init; }
    public bool?    DownloadAsPdf { get; init; }
    public string?  FolderPath   { get; init; } // folder_mini.full_path - the curated tree
    public string?  FolderName   { get; init; } // folder_mini.folder_name - the leaf
    public int?     FolderId     { get; init; }
    public string?  Language     { get; init; } // Zenya's authored language - FORMAT UNMEASURED, so not substituted for the detected one

    // ── Validity / review ──────────────────────────────────────────────────────────────────
    public DateTimeOffset?       CheckDate      { get; init; } // next review due - NOT "stops applying"
    public string?               CheckDateRaw   { get; init; } // kept so an unparseable value is visible, not lost
    public IReadOnlyList<string> AttentionFlags { get; init; } = [];
    public bool?                 CanCheckDocument { get; init; }
    public string?               CheckDelegatedTo { get; init; }
    public DateTimeOffset?       LastModified    { get; init; } // when Zenya says the content last changed
    public string?               LastModifiedRaw { get; init; }

    // ── Header ─────────────────────────────────────────────────────────────────────────────
    public string? ParsedHeader        { get; init; }
    public string? UnparsedHeader      { get; init; }
    public bool?   PrintHeaderRequired { get; init; }

    // ── Persons (D204 §3d: carried, never Search-indexed) ──────────────────────────────────
    public IReadOnlyList<string> Authors                { get; init; } = [];
    public IReadOnlyList<string> Authorizers            { get; init; } = [];
    public IReadOnlyList<string> DocumentAdministrators { get; init; } = [];
    public IReadOnlyList<string> WritersGroup           { get; init; } = [];
    public IReadOnlyList<string> InvitedWriters         { get; init; } = [];

    // ── Authoring / UI state (rides on the document, stops there) ──────────────────────────
    public bool?   Locked       { get; init; }
    public string? LockedSince  { get; init; }
    public string? LockedBy     { get; init; }
    public bool?   MarkedAsFavorite { get; init; }
    public bool?   IsPrintable  { get; init; }
    public bool?   IsEditableForm { get; init; }
    public bool?   ShowInOfficeOnlineViewer { get; init; }
    public string? OfficePrintCoverPageMode { get; init; }
    public bool?   HasDeletePublishedPermission { get; init; }
    public string? WriterHandInDeadline { get; init; }
    public DateTimeOffset? SyncedAt { get; init; }

    // True when the blob was written by the sync at all. A blob from the manual corpus has none
    // of the keys, and callers that want "did Zenya say anything" should ask this, not Version.
    public bool IsPresent => DocumentId is not null;

    public static ZenyaMetadata FromBlobMetadata(IReadOnlyDictionary<string, string> m)
    {
        var lastModifiedRaw = Raw(m, ZenyaBlobLayout.LastModifiedKey);
        var checkDateRaw    = Raw(m, ZenyaBlobLayout.CheckDateKey);
        return new ZenyaMetadata
        {
            DocumentId   = Raw(m, ZenyaBlobLayout.DocumentIdKey),
            Version      = Int(m, ZenyaBlobLayout.VersionKey),
            Revision     = Int(m, ZenyaBlobLayout.RevisionKey),
            Status       = Raw(m, ZenyaBlobLayout.StatusKey),
            Active       = Bool(m, ZenyaBlobLayout.ActiveKey),
            Url          = Raw(m, UrlKey),

            QuickCode    = Decoded(m, ZenyaBlobLayout.QuickCodeKey),
            Title        = Decoded(m, ZenyaBlobLayout.TitleKey),
            Summary      = Decoded(m, ZenyaBlobLayout.SummaryKey),
            Type         = Decoded(m, ZenyaBlobLayout.TypeKey),
            DocumentType = Decoded(m, ZenyaBlobLayout.DocumentTypeKey),
            MimeType     = Raw(m, ZenyaBlobLayout.MimeTypeKey),
            OriginalType = Decoded(m, ZenyaBlobLayout.OriginalTypeKey),
            DownloadAsPdf = Bool(m, ZenyaBlobLayout.DownloadAsPdfKey),
            FolderPath   = Decoded(m, ZenyaBlobLayout.FolderPathKey),
            FolderName   = Decoded(m, ZenyaBlobLayout.FolderNameKey),
            FolderId     = Int(m, ZenyaBlobLayout.FolderIdKey),
            Language     = Raw(m, ZenyaBlobLayout.LanguageKey),

            CheckDate      = ParseZenyaDate(checkDateRaw),
            CheckDateRaw   = checkDateRaw,
            AttentionFlags = Split(Raw(m, ZenyaBlobLayout.AttentionKey), ','),
            CanCheckDocument = Bool(m, ZenyaBlobLayout.CanCheckKey),
            CheckDelegatedTo = Decoded(m, ZenyaBlobLayout.CheckDelegateKey),
            LastModified    = ParseZenyaDate(lastModifiedRaw),
            LastModifiedRaw = lastModifiedRaw,

            ParsedHeader        = Decoded(m, ZenyaBlobLayout.ParsedHeaderKey),
            UnparsedHeader      = Decoded(m, ZenyaBlobLayout.UnparsedHeaderKey),
            PrintHeaderRequired = Bool(m, ZenyaBlobLayout.PrintHeaderKey),

            Authors                = Names(m, ZenyaBlobLayout.AuthorsKey),
            Authorizers            = Names(m, ZenyaBlobLayout.AuthorizersKey),
            DocumentAdministrators = Names(m, ZenyaBlobLayout.AdministratorsKey),
            WritersGroup           = Names(m, ZenyaBlobLayout.WritersGroupKey),
            InvitedWriters         = Names(m, ZenyaBlobLayout.InvitedWritersKey),

            Locked       = Bool(m, ZenyaBlobLayout.LockedKey),
            LockedSince  = Raw(m, ZenyaBlobLayout.LockedSinceKey),
            LockedBy     = Decoded(m, ZenyaBlobLayout.LockedByKey),
            MarkedAsFavorite = Bool(m, ZenyaBlobLayout.FavoriteKey),
            IsPrintable  = Bool(m, ZenyaBlobLayout.PrintableKey),
            IsEditableForm = Bool(m, ZenyaBlobLayout.EditableFormKey),
            ShowInOfficeOnlineViewer = Bool(m, ZenyaBlobLayout.OfficeViewerKey),
            OfficePrintCoverPageMode = Decoded(m, ZenyaBlobLayout.CoverPageKey),
            HasDeletePublishedPermission = Bool(m, ZenyaBlobLayout.CanDeleteKey),
            WriterHandInDeadline = Raw(m, ZenyaBlobLayout.HandInDeadlineKey),
            SyncedAt = DateTimeOffset.TryParse(Raw(m, ZenyaBlobLayout.SyncedAtKey), CultureInfo.InvariantCulture,
                           DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var synced) ? synced : null,
        };
    }

    // The one key the sync does NOT write. A hand-set deep link on a manually uploaded blob is
    // the only producer, and D094 explains why there is no constructed value.
    private const string UrlKey = "zenya_url";

    // Fail open: a blob nobody has annotated (the whole manual corpus) is treated as active, not
    // excluded. On a synced blob `active` is authoritative and the status-string list below is
    // only the fallback for a blob that carries a status but no flag - see
    // CompareSourceListingToIndex's use of this. The list is the pre-2026-09-21 rule, kept
    // verbatim so behaviour on such a blob does not change.
    private static readonly string[] InactiveStatuses = ["ingetrokken", "vervangen", "inactive", "withdrawn", "replaced"];

    public bool IsActive => Active ?? (Status is null || !InactiveStatuses.Contains(Status, StringComparer.OrdinalIgnoreCase));

    // Zenya's two documented date shapes (D155 §1): `yyyyMMdd` for dates, `yyyyMMddHHmmss` for
    // datetimes, both UTC. Exact-format only: anything else is null, never a guess - the raw
    // string is kept next to the parsed value so the failure is visible.
    private static readonly string[] ZenyaDateFormats = ["yyyyMMddHHmmss", "yyyyMMdd"];

    public static DateTimeOffset? ParseZenyaDate(string? raw) =>
        !string.IsNullOrWhiteSpace(raw)
        && DateTimeOffset.TryParseExact(raw.Trim(), ZenyaDateFormats, CultureInfo.InvariantCulture,
               DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;

    // ── decoding helpers ─────────────────────────────────────────────────────────────────────

    private static string? Raw(IReadOnlyDictionary<string, string> m, string key) =>
        m.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    // Uri.UnescapeDataString is the inverse of the writer's Uri.EscapeDataString. A raw value
    // that was never escaped passes through unchanged, so a hand-set plain-ASCII value still reads.
    private static string? Decoded(IReadOnlyDictionary<string, string> m, string key) =>
        Raw(m, key) is { } v ? Uri.UnescapeDataString(v) : null;

    private static int? Int(IReadOnlyDictionary<string, string> m, string key) =>
        int.TryParse(Raw(m, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;

    // The writer emits exactly "true"/"false"; anything else is null, not false.
    private static bool? Bool(IReadOnlyDictionary<string, string> m, string key) =>
        Raw(m, key) switch { "true" => true, "false" => false, _ => null };

    private static IReadOnlyList<string> Names(IReadOnlyDictionary<string, string> m, string key) =>
        Split(Decoded(m, key), ';');

    private static IReadOnlyList<string> Split(string? value, char separator) =>
        value is null
            ? []
            : value.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
