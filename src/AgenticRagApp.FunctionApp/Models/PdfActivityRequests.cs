namespace AgenticRagApp.Functions;

// Durable activity payload contract for PdfIndexingFunction's orchestrator.
//
// Deliberately NOT shared with any other pipeline - these are PDF's own. A second
// indexing pipeline declares its own request records rather than reusing these, even
// where the shape happens to match today. The previous unprefixed names (IndexRequest,
// ExtractRequest, ...) invited exactly that reuse, and the shapes then diverged anyway:
// PDF moved stale document IDs from an inline list to a blob reference, which is why
// PdfExtractRequest/PdfEmbedUploadRequest carry StaleIdsBlob (a blob name) rather than
// the IReadOnlyList<string> an older caller passed.
//
// Every activity receives its own blob-name-keyed request record rather than the full
// working state, per the payload-by-blob-name pattern documented on PdfIndexingFunction:
// only the blob name travels through Durable Table Storage, avoiding the 64KB row-size limit.
public record PdfIndexRequest(bool ForceReindex);
public record PdfExtractRequest(bool ForceReindex, string OutputBlob, string StaleIdsBlob, string InstanceId, DateTimeOffset StartedAt);
public record PdfChunkRequest(string InputBlob, string OutputBlob, string FamilyMovesBlob, string InstanceId, DateTimeOffset StartedAt);
public record PdfEmbedUploadRequest(string ChunksBlob, string StaleIdsBlob, string FamilyMovesBlob, string InstanceId, DateTimeOffset StartedAt);
