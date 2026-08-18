using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

public interface IUploadService
{
    // staleDocumentIds: documents whose old chunks may now be orphaned (updated or removed
    // upstream). Cleanup runs AFTER upload succeeds and only removes chunk ids that aren't
    // part of what was just uploaded - see UploadService.
    Task<UploadResult> UploadDocumentsAsync(
        IEnumerable<ChunkObject> documents,
        IReadOnlyList<string>         staleDocumentIds,
        CancellationToken             ct = default);
}

public record UploadResult(
    int   DocsUploaded,
    int   DocsFailed,
    int   ChunksRemoved,
    long? IndexDocumentCountSnapshot,
    long? IndexStorageSizeBytesSnapshot,
    IReadOnlyList<string> RedFlags,
    // The drift baseline this run was compared against - i.e. the previous run's index stats,
    // captured before IndexStatsMonitor overwrote them. Null when no baseline existed, or when
    // the stats snapshot itself failed. See IndexDriftCheck.
    long? PreviousIndexDocumentCount    = null,
    long? PreviousIndexStorageSizeBytes = null
);
