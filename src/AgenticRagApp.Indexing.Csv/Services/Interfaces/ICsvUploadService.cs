using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Indexing.Csv.Services;

public interface ICsvUploadService
{
    // staleDocumentIds: documents whose old chunks may now be orphaned (updated or removed
    // upstream). Cleanup runs AFTER upload succeeds and only removes chunk ids that aren't
    // part of what was just uploaded - see CsvUploadService.
    Task<CsvUploadResult> UploadDocumentsAsync(
        IEnumerable<ChunkStatsAdapter> documents,
        IReadOnlyList<string>         staleDocumentIds,
        CancellationToken             ct = default);
}

public record CsvUploadResult(
    int   DocsUploaded,
    int   DocsFailed,
    int   ChunksRemoved,
    long? IndexDocumentCountSnapshot,
    long? IndexStorageSizeBytesSnapshot,
    IReadOnlyList<string> RedFlags
);
