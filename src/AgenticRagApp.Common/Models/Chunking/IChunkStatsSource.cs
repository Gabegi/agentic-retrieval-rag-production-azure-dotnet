namespace AgenticRagApp.Common.Models;

// What ChunkingStageMetrics.Compute needs from a chunk on top of the common IChunk shape.
// Implemented by each pipeline's own chunk type (e.g. ChunkObject) — Observability never
// references those types directly.
public interface IChunkStatsSource : IChunk
{
    bool IsCoherent { get; }

    // The string the size bands, the size extremes and duplicate detection are measured on.
    //
    // Both implementations override it with their EmbeddingText, because both separate the stored
    // body from the text they actually embed - ChunkObject holds the prefix beside Content, the
    // archived CSV pipeline's ChunkStatsAdapter holds the summary beside it. Measured on Content,
    // a size band excludes text that reaches the embedder anyway, and two chunks with identical
    // bodies under different prefixes/summaries count as duplicates despite producing different
    // vectors.
    //
    // It defaults to Content rather than being abstract so that a new chunk type with no such
    // split needs no override, and gets the only sensible answer. If you add a type WITH a split,
    // override it: an inherited default here is silent, and the number it produces is wrong in a
    // way nothing reports.
    //
    // NOT the same decision as IsCoherent, which stays on the bare body deliberately - see
    // ChunkObject.
    string StatsText => Content;

    // The stored tokenizer count of StatsText - the text that gets embedded - or null when the
    // chunk type has no real count. ChunkingStageMetrics.Compute reads it for the token
    // distribution (2026-09-15); it never tokenizes anything itself.
    //
    // Defaults to null, not to a word-count proxy. A proxy in this field would be reported as if
    // it were the model's count, in a way nothing flags - ChunkStatsAdapter.TokenEstimate is
    // exactly such a proxy and stays out. Null reads as "not measured" on the report.
    int? EmbeddedTokenCount => null;

    // Whether the cut carries (part of) a typed table span. Splits tokens-per-word in two: table
    // markup tokenizes at ~2x the rate of prose (chunk-token-metrics-260909-1.md §4), so one
    // ratio over both populations is wrong for each. Defaults to false - a type with no table
    // concept reports everything as prose, which is the honest answer for it.
    bool IsTableShaped => false;

    // Tokenizer count of the prefix prepended before embedding (title line, sector tag, heading
    // chain), or null when the type has no prefix concept. Read once per chunk by Compute for
    // the prefix-share figures - implementations may count on read.
    int? PrefixTokenCount => null;

    // Characters of Content that are figure description (CU writes each figure's generated
    // description into the markdown as image alt text, so it rides inside the chunk), and the
    // subset describing a pageHeader / pageFooter figure - a logo. Null = not measured: no
    // figure concept, or not stamped. Measured against Content, not StatsText, to match
    // cu-figures-demonstration.md §5's 341 / 117 / 23.
    int? FigureTextChars => null;
    int? HeaderFooterFigureTextChars => null;
}
