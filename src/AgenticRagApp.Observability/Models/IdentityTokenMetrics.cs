namespace AgenticRagApp.Observability.Reports;

// Token pressure on the document-identity embeddings, lifted onto the run report from the
// chunking artifact's identity diagnostics (2026-09-15).
//
// Identity text is a document's title plus every heading, embedded as ONE input and uncapped by
// design (a cap changes every identity hash and forces a full re-cluster - DocumentIdentityBuilder).
// Chunks never get near the model's per-input limit; this is the only place it is live, and the
// failure past it is silent: the tail of the heading list simply stops reaching the vector the
// document's family is clustered on. Until this record existed the margin was computed every run
// and written only into the chunking artifact - a file large enough that nobody opened it, so the
// tripwire crossing the 80% line on 260909/1 was found by hand (D191 §4).
//
// The limit and the threshold ride along with the numbers so a row is self-describing when either
// is tuned. Observability cannot reference DocumentIdentityBuilder (dependency runs the other way);
// the caller stamps them.
public sealed record IdentityTokenMetrics(
    // The model's per-input limit the identity text is embedded against.
    int  Limit,
    // Tokens above which a document is listed in NearingLimit (80% of Limit).
    int  WarningThreshold,
    // Largest identity text among THIS RUN's documents - the documents handed to chunking, not
    // the whole live corpus. On an incremental run this is the max over the changed documents.
    int  Max,
    // Identity tokens billed this run: fresh vectors only, reused ones bill nothing. Deliberately
    // NOT part of Embedding.TotalEmbeddingTokens (IdentityEmbedder).
    long TotalEmbedded,
    // Identity tokens of EVERY document this run resolved, cached or not - what a full re-embed
    // of the identity texts would send. TotalEmbedded is the subset this run paid for.
    long TotalThisRun,
    // How many documents are over WarningThreshold. Carried separately because the list below is
    // capped for the Durable row limit; a capped list must not read as the whole count.
    int  NearingLimitCount,
    // Those documents, largest first, capped. Empty is the healthy state.
    IReadOnlyList<IdentityDocTokens> NearingLimit);

public sealed record IdentityDocTokens(string SourceId, int Tokens);
