using AgenticRagApp.Indexing.Pdf.Utils;
using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Indexing.Pdf.Models;

// The whole chunking stage in one blob: identity resolution, strategy routing, heading
// location and the chunks themselves. Written by ChunkingService (not the activity) so it can
// be written from a finally block - a run that throws half way through is exactly the run
// whose report is worth having, and the old chunking-artifact was written only on the success
// path, so a failure produced a stack trace and nothing else.
//
// One report per stage rather than one per step: extraction already sets that precedent
// (extraction-artifact), and splitting identity resolution into its own blob would mean two
// files to correlate for one activity.
//
// Size: carries the full chunk list, like extraction-artifact carries full document content.
// Uncapped on purpose - see PipelineArtifactWriter, which streams rather than buffers.
public sealed record ChunkingRunReport(
    string?         InstanceId,
    DateTimeOffset  StartedAt,
    DateTimeOffset  CompletedAt,

    // False when the stage threw. FailedAtStage says how far it got, which is the difference
    // between "the embedding call failed" and "chunking produced bad output".
    bool            Success,
    string?         FailedAtStage,
    string?         Error,

    // Every input document, one row each, with exactly one outcome - see DocumentOutcome.
    IReadOnlyList<DocumentOutcome>      Documents,

    // Null when identity resolution threw before producing anything.
    IdentityResolutionDiagnostics?      Identity,

    HeadingLocationSummary?             HeadingLocation,
    ChunkingStageMetrics?               Stats,
    IReadOnlyList<ChunkObject>?         Chunks);

// One row per document that entered the stage. Outcome is the single question this report
// exists to answer: what happened to this document, and if it produced nothing, why.
//
// The reasons matter more than the counts. A document dropped by the extraction gate produces
// zero chunks and is absent from every other artifact - which is how 20 of 51 documents were
// missing from the index while every stage still reported success
// (docs/2608/260813/first-run-findings.md §1).
public sealed record DocumentOutcome(
    string   SourceId,
    string?  Title,

    // "chunked"                 - produced at least one chunk
    // "zero_chunks"             - a strategy ran but emitted nothing (or only residue)
    // "failed"                  - the strategy threw on this document; the stage still fails,
    //                             but only after every document was processed and reported
    // "identity_skipped"        - no title and no headings, so nothing to embed or cluster on,
    //                             AND no cuts either. A document with content still chunks -
    //                             the recursive route needs neither - and reports "chunked";
    //                             its null FamilyId is what says identity was skipped for it.
    // "not_reached"             - the stage threw before this document was processed
    // ("no_strategy" appears only in reports written before 260814, when the extraction gate
    //  still dropped whole documents instead of flagging them - see FailedExtractionGate.)
    string   Outcome,
    string?  Reason,

    // The extraction gate's verdict, demoted from filter to flag on 260814: true marks a
    // document whose content likely lives in images (candidate for the Content Understanding
    // branch, E6). It still chunks - this is why 20 of 51 documents stopped vanishing.
    bool     FailedExtractionGate,

    // Units the minimum-content rule removed before indexing (vector residue like the corpus's
    // literal "£ £" chunk). Nonzero is normal on image-heavy documents; a document whose every
    // unit was residue reports outcome "zero_chunks" with the count in Reason.
    int      ResidueChunksDropped,

    // Null on every outcome where identity was not resolved - which is itself the point: a
    // chunk with a null family_id got it from here.
    string?  FamilyId,
    bool     IsInMultiMemberFamily,
    string?  DomainTag,
    IReadOnlyList<string> ConfusableWith,

    // "embedded" (paid for this run), "reused" (identity text unchanged since last run), or
    // null (never got one).
    string?  IdentityVectorSource,

    int      ChunkCount,
    int      HeadingsTotal,
    int      HeadingsLocated,

    // The routing decision, recorded so "why did this document take this route" is answerable
    // from the report alone. Null on rows written before a route was picked (not_reached).
    string?  SizeClass = null,
    string?  Strategy  = null,

    // ── What the cut looks like, per document ───────────────────────────────
    // All read off this document's own chunks after step 4 stamped them, so they cost one pass
    // over a list already in memory. Zero on a document that produced nothing, which reads
    // correctly next to ChunkCount 0 - none of these is a "not measured" value.

    int      SectionCount       = 0,
    // Percentiles of the REAL tokenizer count over the embedded text, prefix included - the
    // same number the 512 ceiling is enforced against, not the prose-derived estimate.
    int      TokenP50           = 0,
    int      TokenP99           = 0,
    // Above SectionSplitter.DefaultTokenCeiling. Non-zero is not automatically a defect:
    // DegradedChunks says how many of them were breached deliberately, because the alternative
    // was splitting a table row or separating a value from its label.
    int      ChunksAboveCeiling = 0,
    int      ShortChunks        = 0,
    int      DegradedChunks     = 0,

    // ── The routing signals, reported rather than routed on ─────────────────

    // Headings the document declared, whatever the route did with them. On a Recursive row this
    // is how many headings the route DISCARDED - which is what E6's Content Understanding work
    // is expected to recover.
    int      HeadingCount       = 0,
    // Table-dominant. Kept as a reported signal after TableChecker stopped influencing routing:
    // atomicity is the splitter's job, not a route.
    bool     IsTableShaped      = false,
    // On the recursive route the title is the ONLY prefix, so an empty title means chunks whose
    // embedded text is bare body with zero identity in the vector. Identity resolution only
    // drops documents with NEITHER title nor headings, so this case survives selection silently.
    bool     EmptyTitle         = false);

// Still without a producer, and deliberately absent rather than added as nullable fields that
// would read as "measured zero": BoundaryLevel counts, CeilingClampEngaged, RealisedOverlap, the
// offset-tie count, and short-chunk-by-cause. All five are produced inside the splitter and
// arrive with that pass.

// What DocumentIdentityResolver did, beyond the per-document rows: the shape of the
// comparison set it worked against, what it excluded and why, and the calibration evidence
// (near misses below the threshold, the word pairs behind each confusable flag). Thresholds
// are included so a report stays self-describing when they are eventually tuned.
public sealed record IdentityResolutionDiagnostics(
    string   EmbeddingModelId,
    int      DocumentsIn,
    int      ComparisonSetSize,
    int      PersistedRecordsLoaded,
    int      PersistedExcludedNoVector,
    int      PersistedExcludedOtherModel,
    int      PersistedExcludedWrongDimensions,
    int      VectorsEmbedded,
    int      VectorsReused,
    IReadOnlyList<string>           SkippedEmptyIdentity,
    // Identity texts are uncapped - every heading goes in - and the failure past the model's
    // per-input limit is a silent truncation, so the margin is reported every run rather than
    // assumed. NearingTokenLimit is empty on a healthy run.
    int                             MaxIdentityTokens,
    int                             TotalIdentityTokensEmbedded,
    IReadOnlyList<IdentityTokenPressure> NearingTokenLimit,
    int                             IdentityTokenLimit,
    IReadOnlyList<FamilyDiagnostic> Families,
    // How each family got its name: kept an existing id (membership changed, the name did
    // not), minted a new one, merged two previously-distinct families, or split off one.
    // Anything other than "kept" is a composition change worth looking at.
    IReadOnlyList<FamilyAssignmentDecision> FamilyAssignments,
    IReadOnlyList<SimilarityPair>   NearMisses,
    IReadOnlyList<ConfusableMatch>  ConfusableMatches,
    // Previously-indexed documents whose stored FamilyId changed because this run's documents
    // merged them into a different cluster. Store-only - their Search chunks keep the old
    // value until they are reindexed (see DocumentIdentityResolver's assign-only note).
    IReadOnlyList<FamilyMove>       FamilyMovesInStore,
    // Identity records actually written this run vs already current. On a run where nothing
    // changed both the embedding call and every store write are skipped, so RecordsWritten
    // near zero is the healthy steady state, not a sign that persistence failed.
    int                             RecordsWritten,
    int                             RecordsUnchanged,
    double   SimilarityThreshold,
    double   NearMissFloor,
    double   ConfusableWordThreshold,
    int      MaxConfusableEdits,
    int      MinConfusableWordLength);

public sealed record FamilyMove(string SourceId, string? FromFamilyId, string ToFamilyId);

// A document whose identity text is approaching the embedding model's per-input token limit.
// Heading-dense documents are the driver: measured, ~19 tokens per heading.
public sealed record IdentityTokenPressure(string SourceId, int Tokens);

// The standing evidence for locating headings by string match rather than rewriting PdfCleaner
// to emit an offset map: that call was made against a measured 1,273/1,273 exact-match rate
// with an escalation threshold fixed in advance at >2%, so the rate is reported every run.
public sealed record HeadingLocationSummary(
    int    HeadingsTotal,
    int    HeadingsLocated,
    double UnlocatedRate,
    bool   ExceedsEscalationThreshold,
    int    PairedZeroBodyHeadingsMerged,

    // Headings that arrived with no DI offset and were ordered by arrival position instead
    // (HeadingLocator.OrderByOffset). Distinct from the unlocated rate above: that one counts
    // headings whose text could not be found in the cleaned content, this one counts headings
    // extraction could not place in the RAW content either. Measured at 0 of 1,273 across the
    // big four, so anything other than zero is a regression upstream, not a chunking result.
    int    HeadingsWithoutOffset = 0);
