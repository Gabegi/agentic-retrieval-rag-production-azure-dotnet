using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

public interface IUploadService
{
    // staleDocumentIds: documents whose old chunks may now be orphaned (updated or removed
    // upstream). Cleanup runs AFTER upload succeeds and only removes chunk ids that aren't
    // part of what was just uploaded - see UploadService.
    //
    // familyMoves: documents this run re-homed into a different family. These are typically NOT
    // in documents - a family move is caused by other documents changing the clustering, so the
    // moved document's own bytes are unchanged and extraction skipped it - which is exactly why
    // they need naming separately. Patched, not re-uploaded; see UploadService.
    Task<UploadResult> UploadDocumentsAsync(
        IEnumerable<ChunkObject>      documents,
        IReadOnlyList<string>         staleDocumentIds,
        IReadOnlyList<FamilyMove>     familyMoves,
        CancellationToken             ct = default);
}

public record UploadResult(
    int   DocsUploaded,
    int   DocsFailed,
    int   ChunksRemoved,
    // Index rows whose family_id was patched without their content being touched. Zero on a run
    // where no document changed family, which is most runs.
    int   ChunkFamiliesPatched,
    long? IndexDocumentCountSnapshot,
    long? IndexStorageSizeBytesSnapshot,
    IReadOnlyList<string> RedFlags,
    // The drift baseline this run was compared against - i.e. the previous run's index stats,
    // captured before IndexStatsMonitor overwrote them. Null when no baseline existed, or when
    // the stats snapshot itself failed. See IndexDriftCheck.
    long? PreviousIndexDocumentCount    = null,
    long? PreviousIndexStorageSizeBytes = null
);
