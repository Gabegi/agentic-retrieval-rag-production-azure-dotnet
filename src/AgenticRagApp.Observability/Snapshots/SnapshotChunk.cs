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
    string? Heading,
    int PageNumber,
    int ChunkIndex,
    string ContentHash)
{
    public static SnapshotChunk From<T>(T doc) where T : ISnapshotSource => new(
        Id:               doc.Id,
        DocumentId:       doc.DocumentId,
        Title:            doc.Title,
        LastModifiedDate: doc.LastModifiedDate,
        Content:          doc.Content,
        Heading:          doc.Heading,
        PageNumber:       doc.PageNumber,
        ChunkIndex:       doc.ChunkIndex,
        ContentHash:      doc.ContentHash);
}
