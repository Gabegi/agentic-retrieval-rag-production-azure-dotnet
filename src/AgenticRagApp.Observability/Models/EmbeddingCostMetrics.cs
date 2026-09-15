namespace AgenticRagApp.Observability.Reports;

// What this run's embeddings cost, in tokens and money (2026-09-15).
//
// Why it exists: the tokens were already measured but split across two stages by design -
// Embedding.TotalEmbeddingTokens is the chunk embeddings, Chunking.IdentityTokens.TotalEmbedded
// is the document-identity embeddings, and report-schema.md deliberately refuses to fold the
// second into the first. That is right for reading a stage, and wrong for the one question
// anybody actually asks a run report: what did this run send to the API. This record is that
// rollup, and it says which parts it is made of rather than replacing either.
//
// Why the rate is carried and not just the dollars: report-schema.md's cost section refused to
// store money at all, on the grounds that list price "changes without a code change". That
// objection is answered by configuration rather than by omission - but only if the rate travels
// WITH the number it produced. A report holding $0.14 and nothing else cannot be compared to one
// from two months ago, because neither says which rate it used. RateUsdPer1M makes every report
// self-describing, and a rate change is an app-setting edit, not a redeploy.
//
// Deliberately NOT a tier enum. The indexing path calls GenerateAsync synchronously; there is no
// batch submission anywhere in this codebase, so a "batch" label on a report would describe
// something the code does not do. One rate, set to whatever tier is actually in use.
public sealed record EmbeddingCostMetrics(
    // Tokens actually sent to the embedding API this run, both stages together. Cache hits are
    // NOT in here - they billed nothing. Null when neither stage reported usage: blank, not zero.
    long?   TokensEmbedded,
    // The two parts of the rollup, each null when that stage reported no usage.
    long?   ChunkTokens,
    long?   IdentityTokens,
    // The configured list rate this run's cost was computed at, in USD per 1M input tokens.
    decimal RateUsdPer1M,
    // TokensEmbedded x RateUsdPer1M / 1M. Null whenever TokensEmbedded is null - never 0, or a
    // run whose usage went unreported would read as a free run.
    decimal? CostUsd,

    // ── What a full re-embed would cost ──────────────────────────────────────────────────────
    // Every chunk this run produced, cached or not (Chunking.Tokens.Total) plus every identity
    // text it resolved (IdentityTokens.TotalThisRun) - i.e. the bill if the vector cache were
    // cold. This is the number that matters when weighing a chunker or model change, because
    // such a change invalidates the cache by definition. Null when the token metrics are absent.
    long?    FullReEmbedTokens,
    decimal? FullReEmbedCostUsd,

    // ── The prefix's share of that ───────────────────────────────────────────────────────────
    // Tokens spent on the title / sector tag / heading chain carried by every chunk, and what
    // they cost at the same rate (Chunking.Tokens.PrefixTokensTotal). It buys retrieval context
    // and costs zero index bytes - a vector is the same width whatever text produced it - so this
    // is the whole of what the prefix costs. Null when no chunk carried a prefix count.
    long?    PrefixTokens,
    decimal? PrefixCostUsd,
    double?  PrefixShareOfTokens)
{
    // Rounded to cents-of-a-cent: a whole index rebuild lands well under a dollar at current
    // rates, so 2dp would round the entire run away.
    private const int CostPrecision = 4;

    public static EmbeddingCostMetrics? From(
        long? chunkTokens,
        long? identityTokens,
        long? fullReEmbedTokens,
        long? prefixTokens,
        double? prefixShareOfTokens,
        decimal rateUsdPer1M)
    {
        // Null rate is not representable (decimal), but a non-positive one is: treat it as "not
        // configured" rather than reporting every run as free.
        if (rateUsdPer1M <= 0) return null;

        // Sum of what was reported. Null + 5 is 5, not null: one stage reporting no usage must
        // not blank out the other's measurement.
        long? embedded = (chunkTokens, identityTokens) switch
        {
            (null, null) => null,
            var (c, i)   => (c ?? 0) + (i ?? 0),
        };

        return new EmbeddingCostMetrics(
            TokensEmbedded:      embedded,
            ChunkTokens:         chunkTokens,
            IdentityTokens:      identityTokens,
            RateUsdPer1M:        rateUsdPer1M,
            CostUsd:             Cost(embedded, rateUsdPer1M),
            FullReEmbedTokens:   fullReEmbedTokens,
            FullReEmbedCostUsd:  Cost(fullReEmbedTokens, rateUsdPer1M),
            PrefixTokens:        prefixTokens,
            PrefixCostUsd:       Cost(prefixTokens, rateUsdPer1M),
            PrefixShareOfTokens: prefixShareOfTokens);
    }

    private static decimal? Cost(long? tokens, decimal ratePer1M) =>
        tokens is { } t ? Math.Round(t / 1_000_000m * ratePer1M, CostPrecision) : null;
}
