namespace AgenticRagApp.Indexing.CU.Models;

// Zenya's own identity/lifecycle facts for a PDF - never present in the PDF's own bytes
// (confirmed empirically: Zenya's Documentgegevens fields - Snelcode, Versie, etc. - don't
// appear anywhere in a sample file's Info dictionary, content streams, or XMP packet).
// Two sources write these keys: hand-set metadata on the manually uploaded "documents" corpus
// (rare, optional) and - since 2026-09-11 - the ZenyaSync tool, which stamps every blob it
// writes into the "zenya-documents" container (Infrastructure/Clients/Zenya/Sync/ZenyaBlobLayout,
// D185). The indexer still reads "documents" today; when it is pointed at zenya-documents these
// values become authoritative and this record should grow the extra keys ZenyaBlobLayout writes
// (zenya_quick_code, zenya_title - percent-encoded, zenya_last_modified). Missing metadata stays
// the expected default on the manual corpus - see IsActive below and ExtractionDocument.s comment.
public record ZenyaMetadata(
    string? DocumentId,
    string? Version,
    string? Status,
    string? Url)
{
    public static readonly ZenyaMetadata Empty = new(null, null, null, null);

    private const string DocumentIdKey = "zenya_document_id";
    private const string VersionKey    = "zenya_version";
    private const string StatusKey     = "zenya_status";
    private const string UrlKey        = "zenya_url";

    public static ZenyaMetadata FromBlobMetadata(IReadOnlyDictionary<string, string> metadata) => new(
        DocumentId: metadata.GetValueOrDefault(DocumentIdKey),
        Version:    metadata.GetValueOrDefault(VersionKey),
        Status:     metadata.GetValueOrDefault(StatusKey),
        Url:        metadata.GetValueOrDefault(UrlKey));

    // Fail open: a blob nobody has annotated yet (the common case today, since this is a
    // manual step) is treated as active, not excluded. Only an explicit, recognised
    // inactive-style status value blocks a document from being indexed/kept indexed - see
    // CompareSourceListingToIndex's use of this.
    private static readonly string[] InactiveStatuses = ["ingetrokken", "vervangen", "inactive", "withdrawn", "replaced"];

    public bool IsActive => Status is null || !InactiveStatuses.Contains(Status, StringComparer.OrdinalIgnoreCase);
}
