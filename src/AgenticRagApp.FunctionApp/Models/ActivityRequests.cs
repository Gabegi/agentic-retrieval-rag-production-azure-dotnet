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

// RecreateIndex drops and rebuilds the index (and the knowledge source/base on top of it)
// empty before extraction starts, rather than indexing into whatever is already there.
// Optional with a false default deliberately: an orchestration queued by an earlier
// deployment has no such property in its persisted JSON input, and must still deserialize.
public record IndexRequest(bool ForceReindex, bool RecreateIndex = false);
public record ExtractRequest(bool ForceReindex, string OutputBlob, string StaleIdsBlob, string InstanceId, DateTimeOffset StartedAt);
public record ChunkRequest(string InputBlob, string OutputBlob, string FamilyMovesBlob, string InstanceId, DateTimeOffset StartedAt);
// VectorDimensions is the LIVE index field width, read once at preflight and threaded down rather
// than re-read or taken from configuration (D201). It is what the embed and upload stages judge a
// vector against, because the index is the only thing that can actually reject one.
public record EmbedUploadRequest(string ChunksBlob, string StaleIdsBlob, string FamilyMovesBlob, string InstanceId, DateTimeOffset StartedAt, int VectorDimensions);
