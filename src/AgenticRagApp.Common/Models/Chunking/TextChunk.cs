namespace AgenticRagApp.Common.Models;

// Output of the low-level splitter, identical in both pipelines.
// EstimatedTokens: chars/token ratio estimate computed at chunk-creation time (see
// AgenticRagApp.Indexing.CU.Utils.ChunkingHelper.EstimateTokens), using the measured
// per-segment ratio (docs/2608/260811/tokenizer-redo-findings.md) rather than one blended
// number - the ratio isn't constant (prose ~3.1-3.3 vs table ~1.9-2.8 chars/token), so it
// can't be reconstructed later from Content.Length alone. Defaults to 0 for callers that
// don't compute it (most test fixtures) - never used to gate anything today, so an
// uncomputed 0 is inert rather than misleading.
public sealed record TextChunk(int Index, string Content, string? Heading = null, int EstimatedTokens = 0);
