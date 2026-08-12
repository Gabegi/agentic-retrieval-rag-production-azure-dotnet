using AgenticRagApp.Common.Models;
namespace AgenticRagApp.Observability.Reports;

// One row per chunk believed to be live in the Search index right now, for one doc-type
// pipeline. The rolling snapshot
// (pipeline-reports/{yyyy}/{MM}/{dd}/{ts}-snapshot-{source}-{instanceId}.json, found via the
// _latest-snapshot-{source}.json pointer - see SnapshotService)
// is the union of these across every run of that pipeline, not a per-run diff — and never
// mixes chunks from a different source. Carries everything a future rebuild would need to
// bulk-upsert directly into a fresh index - the real fields UploadService sends to Search,
// plus ContentHash so the vector can be resolved from the vector cache without re-embedding.
public record SnapshotChunk(
    string Id,
    string DocumentId,
    string? Title,
    DateTimeOffset? LastModifiedDate,
    string Content,
    // Names follow IChunk's vocabulary (action-plan.md §4.6). This changes the snapshot's
    // wire format, so snapshots written before the rename do not restore - acceptable
    // because the field rename lands with a full index rebuild anyway, and a snapshot only
    // describes what is currently live in the index.
    string? HeadingText,
    int PageStart,
    int ChildIndex,
    string ContentHash)
{
    public static SnapshotChunk From<T>(T doc) where T : ISnapshotSource => new(
        Id:               doc.Id,
        DocumentId:       doc.DocumentId,
        Title:            doc.Title,
        LastModifiedDate: doc.LastModifiedDate,
        Content:          doc.Content,
        HeadingText:      doc.HeadingText,
        PageStart:        doc.PageStart,
        ChildIndex:       doc.ChildIndex,
        ContentHash:      doc.ContentHash);
}
