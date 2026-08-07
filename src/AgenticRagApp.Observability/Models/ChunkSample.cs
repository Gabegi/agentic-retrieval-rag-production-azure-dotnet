namespace AgenticRagApp.Observability.Reports;

// One chunk, captured verbatim (but truncated) for the run report so a reader can judge
// chunk quality directly instead of inferring it from size/coherence counts.
//
// Size discipline matters here: stage metrics travel as a Durable activity return value,
// which is subject to the same 64KB row limit that already caps ExtractionStageMetrics.Issues
// at 100. Every producer of these samples must cap the count (see ChunkingStageMetrics'
// MaxSamples/MaxExcerptChars) - this type is deliberately small and Content is an excerpt,
// never the whole chunk.
public sealed record ChunkSample(
    string  DocumentId,
    int     PageNumber,
    int     ChunkIndex,
    string? Heading,
    int     SizeChars,
    // First MaxExcerptChars of the chunk. Suffixed with an ellipsis when truncated, so a
    // reader can tell "this chunk is short" from "this excerpt is clipped" - the whole point
    // of showing an undersized chunk is lost if a clipped one looks identical to it.
    string  ContentExcerpt,
    bool    Truncated);

// A chunk body that appeared more than once in a run, identified by content hash. Duplicates
// waste vector space and produce duplicate retrieval hits, and the count alone (DuplicateChunks)
// never says which content is repeated.
public sealed record DuplicateChunkSample(
    string ContentHash,
    int    Occurrences,
    string ContentExcerpt,
    bool   Truncated);
