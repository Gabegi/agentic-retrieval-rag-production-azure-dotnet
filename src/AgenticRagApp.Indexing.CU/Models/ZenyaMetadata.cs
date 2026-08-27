namespace AgenticRagApp.Indexing.CU.Models;

// Zenya's own identity/lifecycle facts for a PDF - never present in the PDF's own bytes
// (confirmed empirically: Zenya's Documentgegevens fields - Snelcode, Versie, etc. - don't
// appear anywhere in a sample file's Info dictionary, content streams, or XMP packet).
// Since there's no automated Zenya -> blob sync today (PDFs are uploaded manually), this can
// only come from custom blob metadata that whoever uploads a PDF sets by hand, copying the
// values shown on that document's Zenya page. Missing metadata is the expected default state
// for every field, not an error - see IsActive below and ExtractionDocument's own comment.
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
