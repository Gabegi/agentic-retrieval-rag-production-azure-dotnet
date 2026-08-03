using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AgenticRagApp.Observability;

public static class Instrumentation
{
    public const string ActivitySourceName = "AgenticRagApp";
    public const string MeterName          = "AgenticRagApp";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, "1.0.0");
    public static readonly Meter          Meter          = new(MeterName, "1.0.0");

    // ── Extraction ────────────────────────────────────────────────────────────
    // Tags: source (e.g. "csv")

    // Total pages/records extracted from the source before diff filtering.
    public static readonly Counter<long> DocsExtracted =
        Meter.CreateCounter<long>("indexer.docs_extracted", description: "Total records extracted from source before diff filtering");

    // Unchanged docs skipped — incremental indexing working correctly. High ratio is good.
    public static readonly Counter<long> DocsSkipped =
        Meter.CreateCounter<long>("indexer.docs_skipped", description: "Docs skipped because content is unchanged since last run");

    // Net-new docs hitting the index for the first time.
    public static readonly Counter<long> DocsNew =
        Meter.CreateCounter<long>("indexer.docs_new", description: "Docs indexed for the first time");

    // Changed docs: stale chunks deleted then re-indexed.
    public static readonly Counter<long> DocsUpdated =
        Meter.CreateCounter<long>("indexer.docs_updated", description: "Docs re-indexed because source content changed");

    // Stale chunk batches removed for changed docs (before re-insert). Should equal DocsUpdated.
    public static readonly Counter<long> DocsDeleted =
        Meter.CreateCounter<long>("indexer.docs_deleted", description: "Stale chunk batches deleted for changed docs");

    // Actual chunk rows deleted from the index for changed docs — the real count behind DocsDeleted,
    // since one changed doc can own many chunks. Nets against DocsUploaded to see true corpus growth.
    public static readonly Counter<long> ChunksRemoved =
        Meter.CreateCounter<long>("indexer.chunks_removed", description: "Chunk rows deleted from the index for changed docs");

    // Validation errors and warnings from PipelineValidator. Tags: source, severity (error|warning), stage.
    public static readonly Counter<long> ValidationIssues =
        Meter.CreateCounter<long>("indexer.validation_issues", description: "Validation issues by severity and stage");

    // Docs past their check_date — live but potentially stale guidance in the index.
    public static readonly Counter<long> StaleDocs =
        Meter.CreateCounter<long>("indexer.stale_docs", description: "Docs flagged check_date_exceeded — guidance may be outdated");

    // Docs with no markdown headings — chunked without structural guidance; may produce lower-quality chunks.
    public static readonly Counter<long> DocsWithoutHeadings =
        Meter.CreateCounter<long>("indexer.docs_without_headings", description: "Docs with no markdown headings — chunked without structural guidance");

    // Pages where known mojibake patterns were auto-repaired. Non-zero is expected on
    // Dutch text with accented characters; watch for a run-over-run jump, not the absolute count.
    public static readonly Counter<long> MojibakeRepairedPages =
        Meter.CreateCounter<long>("indexer.mojibake_repaired_pages", description: "Pages where known mojibake patterns were auto-repaired");

    // Total table-like blocks detected this run (histogram over runs, not per-doc). No ground
    // truth for "expected" count exists yet — watch for a drop vs. recent runs, same as
    // IndexDocumentCount below; a table getting flattened into prose surfaces here as a dip.
    public static readonly Histogram<long> DetectedTableCount =
        Meter.CreateHistogram<long>("indexer.detected_table_count", unit: "tables", description: "Total markdown table blocks detected across all cleaned pages this run");

    // Docs missing key metadata fields. Tags: source, field (title|version|department).
    // Missing title is the worst: it is prepended to every chunk and is the primary BM25 signal.
    public static readonly Counter<long> MissingMetadata =
        Meter.CreateCounter<long>("indexer.missing_metadata", description: "Docs missing key metadata fields (title, version, department)");

    // ── PDF Extraction (Document Intelligence) ──────────────────────────────────

    // 429 throttle retries while polling a DI analyze operation. A spike here means
    // you're hitting Document Intelligence rate limits — see MaxExtractionParallelism
    // in PdfExtractionOrchestrator.
    public static readonly Counter<long> DiThrottleRetries =
        Meter.CreateCounter<long>("indexer.di_throttle_retries", description: "Document Intelligence 429 throttle retries while polling an analyze operation");

    // Wall-clock time from submitting a DI analyze call to it completing (includes any
    // throttle backoff). Watch for a rising p95 as a leading indicator of throttling.
    public static readonly Histogram<double> DiAnalyzeDuration =
        Meter.CreateHistogram<double>("indexer.di_analyze_duration_seconds", unit: "s", description: "Wall-clock time for one Document Intelligence analyze call, submit to completion");

    // Sum of PdfExtractionResult.EstimatedCostUsd across a run - for a pipeline whose
    // dominant operational risk is per-page billing, "how much did this run cost" should
    // be the easiest number to obtain; previously computed per file and then dropped
    // entirely (finding #10).
    public static readonly Counter<double> DiEstimatedCostUsd =
        Meter.CreateCounter<double>("indexer.di_estimated_cost_usd", unit: "USD", description: "Estimated Document Intelligence cost for this run, summed across all analyzed files");

    // ── Chunking ─────────────────────────────────────────────────────────────
    // Tags: strategy (chunking strategy name)

    // Per-chunk character length histogram. Azure Monitor computes p50/p95/max automatically.
    // Watch p95: near 1500 is healthy for this strategy; >> 1500 means truncation risk at embedding.
    public static readonly Histogram<long> ChunkSizeChars =
        Meter.CreateHistogram<long>("indexer.chunk_size_chars", unit: "chars", description: "Character length of each individual chunk");

    // Explicit size-band counter for dashboard distribution charts. Tags: strategy, band.
    // BandUnder100 = noise. Band1500Plus = approaching embedding truncation limit.
    public static readonly Counter<long> ChunkSizeBand =
        Meter.CreateCounter<long>("indexer.chunk_size_band", description: "Chunk count per size band (under_100 / 100_to_500 / 500_to_1500 / 1500_plus)");

    // Docs that produced zero chunks — empty or whitespace-only content after extraction.
    public static readonly Counter<long> DocsWithZeroChunks =
        Meter.CreateCounter<long>("indexer.docs_with_zero_chunks", description: "Docs that produced no chunks — likely empty content");

    // Duplicate chunks (identical content within the same run). Non-zero wastes vector space.
    public static readonly Counter<long> DuplicateChunks =
        Meter.CreateCounter<long>("indexer.duplicate_chunks", description: "Chunks with content identical to another chunk in the same run");

    // Total chunks produced per run (histogram over runs, not per-chunk). Kept for historical continuity.
    public static readonly Histogram<long> ChunksExtracted =
        Meter.CreateHistogram<long>("indexer.chunks_extracted", unit: "chunks", description: "Total chunks produced per indexing run");

    // Chunks that start with uppercase/digit AND end with punctuation — proxy for clean sentence boundaries.
    public static readonly Counter<long> CoherentChunks =
        Meter.CreateCounter<long>("indexer.chunks_coherent", description: "Chunks with clean sentence start and end boundaries");

    // Chunks with a heading field set — benefit from structural context in retrieval.
    public static readonly Counter<long> HeadingsDetected =
        Meter.CreateCounter<long>("indexer.chunks_with_headings", description: "Chunks with a heading field set");

    // ── Embedding ────────────────────────────────────────────────────────────

    // Chunks served from the vector cache instead of a paid embedding call — the whole
    // point of Stage 3's hash-based dedup. High relative to total chunks means it's working.
    public static readonly Counter<long> VectorCacheHits =
        Meter.CreateCounter<long>("indexer.vector_cache_hits", description: "Chunks whose vector was reused from the cache instead of re-embedded");

    // 429 throttle retries. A spike here means you are hitting OpenAI rate limits.
    public static readonly Counter<long> EmbeddingRetries =
        Meter.CreateCounter<long>("indexer.embedding_retries", description: "OpenAI 429 throttle retries during embedding");

    // Chunks over 24k chars truncated before embedding. The model sees incomplete content — quality is degraded.
    public static readonly Counter<long> ChunksTruncated =
        Meter.CreateCounter<long>("indexer.chunks_truncated", description: "Chunks truncated to 24k chars before embedding — model saw incomplete content");

    // Wrong vector dimensions — should always be zero. Non-zero means a model or config mismatch.
    public static readonly Counter<long> VectorDimErrors =
        Meter.CreateCounter<long>("indexer.vector_dim_errors", description: "Chunks with unexpected embedding vector dimensions");

    // ── Upload ────────────────────────────────────────────────────────────────

    // Individual chunk upserts that succeeded. Pair with UploadFailures for the full picture.
    public static readonly Counter<long> DocsUpserted =
        Meter.CreateCounter<long>("indexer.docs_upserted", description: "Individual chunk upserts that succeeded in Azure AI Search");

    // Number of 1000-doc batches submitted to the Search push API.
    public static readonly Counter<long> UploadBatchCount =
        Meter.CreateCounter<long>("indexer.upload_batch_count", description: "Number of 1000-doc batches sent to Azure AI Search");

    // Individual chunk upsert failures. Kept from the original implementation.
    public static readonly Counter<long> UploadFailures =
        Meter.CreateCounter<long>("indexer.upload_failures", description: "Individual document upload failures");

    // Successful full pipeline runs. Kept from the original implementation.
    public static readonly Counter<long> BlobsProcessed =
        Meter.CreateCounter<long>("indexer.blobs_processed", description: "Pipeline runs completed successfully");

    // Unhandled exceptions per pipeline stage. Tags: stage (extract|chunk|embed_upload).
    // Distinct from UploadFailures (per-document) — this fires once per stage crash.
    public static readonly Counter<long> PipelineFailures =
        Meter.CreateCounter<long>("indexer.pipeline_failures", description: "Unhandled exceptions per pipeline stage (tag: stage)");

    // ── Index Stats ──────────────────────────────────────────────────────────
    // Whole-index aggregates from Azure Search, recorded once per run (histogram over runs, not per-doc).
    // Unlike IndexRunReport, these are exported in every environment — the basis for drift dashboards/alerts.

    public static readonly Histogram<long> IndexDocumentCount =
        Meter.CreateHistogram<long>("indexer.index_document_count", description: "Total documents in the Azure Search index after this run's upload");

    public static readonly Histogram<long> IndexStorageSizeBytes =
        Meter.CreateHistogram<long>("indexer.index_storage_size_bytes", unit: "bytes", description: "Total storage size of the Azure Search index after this run's upload");
}
