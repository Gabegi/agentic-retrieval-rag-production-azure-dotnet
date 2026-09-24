namespace AgenticRagApp.Infrastructure.Clients.Zenya.Sync;

// One run's outcome. Every number here is what A8 (D175) asked to measure on the first full
// run; the host prints them and exits non-zero when Failed > 0.
public sealed record ZenyaSyncResult(
    bool DryRun,
    int Listed,             // documents Zenya returned in the listing (published, active, not archived)
    int New,                // downloaded and written for the first time (dry run: would be)
    int Changed,            // Zenya version differs from the blob's zenya_version - rewritten
    int Unchanged,          // same version already in the container - no metadata call, no download
    int Removed,            // blobs whose document dropped out of the listing - deleted
    int AuthoredSkipped,    // can_download_content only, no binary - A9 route, not written
    int NotDownloadable,    // neither flag set - nothing Zenya lets us fetch
    int Failed,             // per-document errors; the run continued past them
    int ForeignBlobs,       // blobs in the container without zenya_document_id - reported, never touched
    int PdfWithoutMagic,    // routed to pdf/ by content type but the bytes do not start with %PDF (D173 q3)
    int MetadataDropped,    // metadata keys removed to fit Azure's 8 KiB cap (D204); >0 means a blob is missing a field
    int Harvested,          // meta/{id}.json sidecars written this run (D243) - new, changed, and unchanged under --reharvest
    int HarvestFailed,      // sidecars that could not be written; the document itself still synced
    long BytesDownloaded,
    IReadOnlyDictionary<string, int> WrittenByExtension,
    IReadOnlyList<ZenyaSyncFailure> Failures,
    TimeSpan Elapsed)
{
    public int Written => New + Changed;
}

public sealed record ZenyaSyncFailure(string DocumentId, string? Title, string Stage, string Error);
