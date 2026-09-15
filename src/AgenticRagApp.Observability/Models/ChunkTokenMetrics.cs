namespace AgenticRagApp.Observability.Reports;

// Token distribution of a run's chunks, on the embedded text (prefix + body) - the basis the
// ceiling is budgeted in and the one metadata.token_count stores. Built from the counts the
// chunks already carry (IChunkStatsSource.EmbeddedTokenCount); nothing here tokenizes.
//
// Why it exists (2026-09-15): ChunkingStageMetrics' size fields are in characters, and
// chars/token is not constant (prose ~3.1-3.3, table markdown ~1.9-2.8), so the one question the
// ceiling is actually set in - how full are the chunks, in tokens - could not be answered from a
// report. docs/2609/260914/chunk-token-metrics-260909-1.md had to recompute it offline from the
// chunking artifact.
//
// The budget-relative counts are against the ceiling the CALLER passed, recorded next to them so
// a row can never be read against a different ceiling than it was measured with - the same
// reason ChunkingBudget exists once. Observability cannot reference ChunkingBudget (the
// dependency runs the other way), hence passed in. Null budget = the caller has no ceiling
// concept, and the three counts stay null rather than being measured against a guess.
public sealed record ChunkTokenMetrics(
    // Chunks that carried a token count. Equal to ChunksProduced on the PDF pipeline; fewer means
    // chunks reached the report without step 4's stamp.
    int    Measured,
    // Sum over Measured. What a full re-embed of this run's chunks would bill.
    long   Total,
    double Mean,
    // Same nearest-rank convention as P95ChunkSizeChars: sorted[(int)(n * q)], clamped.
    int    P50,
    int    P95,
    int    Max,
    // The budget the three counts below were measured against; null when none was supplied.
    int?   Ceiling,
    int?   MinBodyTokenBudget,
    // > Ceiling. The run-level twin of the per-document ChunksAboveCeiling row in the chunking
    // artifact (DocumentRowBuilder) - same predicate on the same stored count.
    int?   AboveCeiling,
    // < Ceiling / 2. The "thin half" line of chunk-token-metrics-260909-1.md §7.
    int?   UnderHalfCeiling,
    // < MinBodyTokenBudget - below the floor a body is allowed to keep against its prefix, i.e.
    // below what the budget itself treats as worth retrieving.
    int?   UnderMinBodyBudget,

    // ── Content per token (2026-09-15) ────────────────────────────────────────────────────────
    // Words = whitespace-separated non-empty runs of the embedded text, chunk-token-metrics
    // -260909-1.md §2's definition, so TokensPerWord is comparable to its 2.92 on the same basis.
    // Not a quality score: a cost and density number - it is what makes 512 tokens buy ~175
    // Dutch words rather than ~390 English ones.
    long    Words,
    double  TokensPerWord,
    // The split behind the ratio. Table-shaped = the cut overlaps a typed table span
    // (IChunkStatsSource.IsTableShaped); table markup tokenizes at ~2x prose, so a table chunk
    // carries about half the content of a prose chunk of the same size. Null ratio = empty
    // population.
    int     TableShapedChunks,
    double? TableTokensPerWord,
    double? ProseTokensPerWord,

    // ── Prefix share (2026-09-15) ─────────────────────────────────────────────────────────────
    // The part of each vector that says title / sector tag / heading chain rather than body.
    // Changing the prefix format changes every embedded string, so every vector - decide it
    // once. Null when no chunk carried a prefix count. Note only the title line and tag are
    // constant across a document; the heading chain varies by section.
    int?    PrefixTokensP50,
    long?   PrefixTokensTotal,
    // PrefixTokensTotal / Total - the share of the run's embedded tokens that is prefix.
    double? PrefixShareOfTotal,
    // Median of the per-chunk prefix / tokens ratio.
    double? PrefixShareP50)
{
    public static ChunkTokenMetrics? From(IReadOnlyList<ChunkTokenSample> samples, int? ceiling, int? minBodyTokenBudget)
    {
        if (samples.Count == 0) return null;

        var sorted = samples.Select(s => s.Tokens).Order().ToArray();
        int At(double quantile) => sorted[Math.Min((int)(sorted.Length * quantile), sorted.Length - 1)];

        var total = sorted.Sum(t => (long)t);
        var words = samples.Sum(s => (long)s.Words);

        var table = samples.Where(s => s.TableShaped).ToList();
        var prose = samples.Where(s => !s.TableShaped).ToList();

        var withPrefix = samples.Where(s => s.PrefixTokens is not null).ToList();
        int?    prefixP50   = null;
        long?   prefixTotal = null;
        double? prefixShare = null, prefixShareP50 = null;
        if (withPrefix.Count > 0)
        {
            var prefixes = withPrefix.Select(s => s.PrefixTokens!.Value).Order().ToArray();
            var shares   = withPrefix.Select(s => s.Tokens > 0 ? s.PrefixTokens!.Value / (double)s.Tokens : 0d).Order().ToArray();
            prefixP50      = prefixes[Math.Min((int)(prefixes.Length * 0.50), prefixes.Length - 1)];
            prefixTotal    = prefixes.Sum(p => (long)p);
            prefixShare    = total > 0 ? prefixTotal / (double)total : null;
            prefixShareP50 = shares[Math.Min((int)(shares.Length * 0.50), shares.Length - 1)];
        }

        return new ChunkTokenMetrics(
            Measured:           sorted.Length,
            Total:              total,
            Mean:               sorted.Average(),
            P50:                At(0.50),
            P95:                At(0.95),
            Max:                sorted[^1],
            Ceiling:            ceiling,
            MinBodyTokenBudget: minBodyTokenBudget,
            AboveCeiling:       ceiling            is { } c ? sorted.Count(t => t > c)     : null,
            UnderHalfCeiling:   ceiling            is { } h ? sorted.Count(t => t < h / 2) : null,
            UnderMinBodyBudget: minBodyTokenBudget is { } m ? sorted.Count(t => t < m)     : null,
            Words:              words,
            TokensPerWord:      Ratio(total, words) ?? 0d,
            TableShapedChunks:  table.Count,
            TableTokensPerWord: Ratio(table.Sum(s => (long)s.Tokens), table.Sum(s => (long)s.Words)),
            ProseTokensPerWord: Ratio(prose.Sum(s => (long)s.Tokens), prose.Sum(s => (long)s.Words)),
            PrefixTokensP50:    prefixP50,
            PrefixTokensTotal:  prefixTotal,
            PrefixShareOfTotal: prefixShare,
            PrefixShareP50:     prefixShareP50);
    }

    private static double? Ratio(long tokens, long words) => words > 0 ? tokens / (double)words : null;
}

// One chunk's contribution to ChunkTokenMetrics, read off IChunkStatsSource by Compute.
public readonly record struct ChunkTokenSample(int Tokens, int Words, bool TableShaped, int? PrefixTokens);
