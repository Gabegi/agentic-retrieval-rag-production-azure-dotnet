using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Indexing.Csv.Services;

public interface IUploadService
{
    // staleDocumentIds: documents whose old chunks may now be orphaned (updated or removed
    // upstream). Cleanup runs AFTER upload succeeds and only removes chunk ids that aren't
    // part of what was just uploaded - see UploadService.
    Task<UploadResult> UploadDocumentsAsync(
        IEnumerable<ChunkStatsSource> documents,
        IReadOnlyList<string>         staleDocumentIds,
        CancellationToken             ct = default);
}

public record UploadResult(
    int   DocsUploaded,
    int   DocsFailed,
    int   ChunksRemoved,
    long? IndexDocumentCountSnapshot,
    long? IndexStorageSizeBytesSnapshot,
    IReadOnlyList<string> RedFlags
);
