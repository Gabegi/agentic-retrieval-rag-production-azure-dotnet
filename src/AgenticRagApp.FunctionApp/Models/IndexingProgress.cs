namespace AgenticRagApp.Functions;

// Durable custom-status payload for the indexing and restore orchestrators - what
// GET /api/index/status reads back, and what the raw Durable statusQueryGetUri now
// carries under "customStatus".
//
// Written only at orchestrator stage boundaries, so it answers "which stage is this run
// in, and what did the finished stages produce" - not "how far through the current stage
// are we". Extraction in particular is a single long-running activity (see the fan-out
// note on PdfIndexingFunction.ExtractActivity), so a run sits on Stage="extracting" for
// as long as extraction takes, with no sub-progress. Adding that needs a progress channel
// out of the activity itself, which custom status can't provide - activities have no
// SetCustomStatus.
//
// Counts are null until the stage that measures them has completed: null means "not
// measured yet", not "measured zero", the same distinction PdfIndexRunReport draws.
public record IndexingProgress(
    string         Stage,
    DateTimeOffset StartedAt,
    int?           DocsExtracted  = null,
    int?           ChunksProduced = null,
    int?           DocsUploaded   = null)
{
    // Stage values. Terminal runs keep their last counts and flip Stage to completed/failed,
    // so a finished run's status still shows what it produced rather than going blank.
    public const string Extracting      = "extracting";
    public const string Chunking        = "chunking";
    public const string EmbedAndUpload  = "embedding+uploading";
    public const string Completed       = "completed";
    public const string Failed          = "failed";

    // Restore pipeline stages - same payload, different stage vocabulary.
    public const string RecreatingIndex = "recreating-index";
    public const string Restoring       = "restoring-from-snapshot";
}
