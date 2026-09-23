using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Observability.Reports;

// Turns a run report into the flag list carried in the run-analysis blob.
//
// Two rules govern everything here:
//
//  1. ACTIONABILITY. If the reader cannot take a specific action, it is not a flag - it is a
//     row in the metrics table. Industry baseline is ~3% of alerts needing action; a list
//     nobody acts on trains people to skip the section entirely.
//
//  2. A NULL STAGE PRODUCES NO FLAGS. Absence of measurement is not a passing measurement. A
//     run that died in extraction must not be reported as having perfect chunking.
//
// Thresholds are either SOURCED (from this codebase or published guidance) or AWAITING
// CALIBRATION. The latter are suppressed while CalibrationMode is on rather than shipped as
// guesses - see RunAnalysisOptions.CalibrationMode.
public static class FlagEvaluator
{
    // Sourced: IndexStatsMonitor.DriftThresholdPct. Critical at double the warn threshold.
    private const double DriftWarnPct     = 0.15;
    private const double DriftCriticalPct = 0.30;

    // Sourced: published embedding-API error-rate guidance (warn >1%, critical >5%).
    private const double EmbeddingRetryWarnRate     = 0.01;
    private const double EmbeddingRetryCriticalRate = 0.05;

    // Sourced: a validation error means malformed source data reached the pipeline. Warn on any,
    // critical once it's a material fraction of the run.
    private const double ValidationErrorCriticalRate = 0.05;
    private const double MissingTitleCriticalRate    = 0.10;

    // Sourced: a healthy run on this corpus measured 11m47s-14m30s on 2026-08-26, so 45 min is
    // several times the observed spread - far enough out that only a genuinely degraded run
    // reaches it.
    //
    // These two were originally pinned to host.json's durableTask.activityFunctionTimeout
    // (60 min) and the 50-min corpus wall clock: past ~70 min the activity was thought to be
    // redelivered and every Content Understanding page re-billed. Both were removed on
    // 2026-09-21 - activityFunctionTimeout is not a Durable option at all (absent from every
    // Durable package binary; see D206) so it never applied, and the wall clock it justified
    // went with it. Only the measured spread above still supports the warn threshold; the
    // 70-min critical is uncalibrated and wants a real slow-run measurement behind it.
    private static readonly TimeSpan ExtractDurationWarn     = TimeSpan.FromMinutes(45);
    private static readonly TimeSpan ExtractDurationCritical = TimeSpan.FromMinutes(70);

    // Sourced: the 2026-08-26 corpus bills 851 standard pages on a full forceReindex. More
    // than double that in one run means the corpus roughly doubled - or something re-billed.
    private const long CuPagesSpikeWarn = 2000;

    // Awaiting calibration - no defensible source. See RunAnalysisOptions.CalibrationMode.
    // The coherence pair (0.70 / 0.50) went with CoherentChunks on 2026-09-23 (D224 A6) and was
    // not replaced: HardCut already has a tripwire in ChunkingService, and Word cuts are readable
    // in Chunking.CutBoundaries.
    private const double UndersizedWarnRate      = 0.10;
    private const double UndersizedCriticalRate  = 0.20;
    private const double OversizedWarnRate       = 0.05;
    private const double DuplicateWarnRate       = 0.02;
    private const double CostMultiplierWarn      = 2.0;

    public static IReadOnlyList<ReportFlag> Evaluate(
        PdfIndexRunReport report,
        FileFactsSummary? fileFacts,
        PreviousRunPointer? previous,
        bool calibrationMode)
    {
        var flags = new List<ReportFlag>();

        EvaluateExtraction(report.Extraction, flags, calibrationMode);
        EvaluateChunking(report.Chunking, flags, calibrationMode);
        EvaluateEmbedding(report.Embedding, report.Chunking, report.StatsReadback, flags);
        EvaluateVectorConfig(report.VectorConfig, flags);
        EvaluateCost(fileFacts, previous, flags, calibrationMode);
        EvaluateRunHealth(report, flags);

        if (calibrationMode)
            flags.RemoveAll(f => f.AwaitingCalibration);

        return flags.OrderByDescending(f => f.Severity).ToList();
    }

    private static void EvaluateExtraction(ExtractionStageMetrics? x, List<ReportFlag> flags, bool calibrationMode)
    {
        if (x is null) return; // stage never ran - no measurement, no flags

        // Unreachable today and knowingly kept: ExtractionOutputBuilder hardcodes
        // ReconciliationProblems = 0 because the validator that counted them is gone and is not
        // coming back (2026-09-09). The field still travels on the report, so the flag stays
        // rather than being deleted and re-derived if anything ever counts this again.
        if (x.ReconciliationProblems > 0)
            flags.Add(new ReportFlag(FlagSeverity.Critical, "Extraction.ReconciliationProblems",
                x.ReconciliationProblems.ToString(), "0",
                "Counts don't add up across stages — a logic bug or data truncation.",
                "Compare Extraction.DocsToProcess against the chunking stage's document count; do not trust this run's other counts until resolved."));

        if (x.ValidationErrors > 0)
        {
            var rate = x.DocsToProcess > 0 ? x.ValidationErrors / (double)x.DocsToProcess : 0;
            var critical = rate > ValidationErrorCriticalRate;
            flags.Add(new ReportFlag(
                critical ? FlagSeverity.Critical : FlagSeverity.Warning,
                "Extraction.ValidationErrors",
                $"{x.ValidationErrors} ({rate:P1} of processed)", "0",
                "Corrupt or malformed source data reached the pipeline.",
                "Check Extraction.Issues for the affected documents and their Reason.Code (Encrypted, MalformedFormat, …)."));
        }

        if (x.MissingTitleCount > 0)
        {
            var rate = x.DocsToProcess > 0 ? x.MissingTitleCount / (double)x.DocsToProcess : 0;
            flags.Add(new ReportFlag(
                rate > MissingTitleCriticalRate ? FlagSeverity.Critical : FlagSeverity.Warning,
                "Extraction.MissingTitleCount",
                $"{x.MissingTitleCount} ({rate:P1} of processed)", "0",
                "Title is prepended to every chunk and is the primary BM25 signal — the most damaging metadata gap.",
                "Ask content owners to set a document title, or extend the filename-derived fallback."));
        }

        // The document-traceability flag that lived here is gone with the source-metadata
        // mechanism it read (2026-08-26): PDF now reports TraceabilityGapCount = null ("no
        // equivalent concept"), so there is nothing to evaluate.

        // Cost telemetry blank (observability plan 4.2, usage_missing). Only meaningful when
        // documents were actually processed - a diff-only run that analyzed nothing has
        // nothing to bill.
        if (x.DocsToProcess > 0 && x.BilledPagesStandard is null)
            flags.Add(new ReportFlag(FlagSeverity.Warning, "Extraction.BilledUsage",
                "blank", "a page count",
                "The run paid for analyses but reported no usage — cost telemetry is blank, not zero.",
                "Check the once-per-host GetUsage warning in the logs (ContentAnalysisClient) for why both the SDK read and the raw fallback found nothing."));

        // Per-model cost telemetry blank (2026-08-27). Sibling of the flag above and separately
        // reachable: the CU meters can arrive while the per-model token map does not, and only
        // the map maps to deployment TPM - contextualization tokens bill a flat 1,000 per page,
        // so they cannot be turned into a load figure. This is the run-analysis half of the
        // warning ExtractionReporter now logs for the same condition.
        if (x.DocsToProcess > 0 && x.BilledTokensByModel.Count == 0)
            flags.Add(new ReportFlag(FlagSeverity.Warning, "Extraction.BilledTokensByModel",
                "blank", "at least one model key",
                "The run paid for analyses but reported no per-model token map — the number that maps to AI-deployment spend and TPM is missing.",
                "Check the once-per-host GetUsage warning in the logs (ContentAnalysisClient); the CU meters alone cannot size extraction parallelism."));

        // Cost spike (plan 4.2, cu_pages_spike - absorbed from the cancelled alert rules).
        if (x.BilledPagesStandard is { } billedPages && billedPages > CuPagesSpikeWarn)
            flags.Add(new ReportFlag(FlagSeverity.Warning, "Extraction.BilledPagesStandard",
                billedPages.ToString("N0"), $"≤ {CuPagesSpikeWarn:N0}",
                "This run billed more than double the full-corpus baseline (851 pages, 2026-08-26).",
                "Confirm the corpus actually grew; otherwise look for a redelivered activity re-billing the run."));

        foreach (var redFlag in x.RedFlags)
            flags.Add(new ReportFlag(FlagSeverity.Warning, "Extraction.RedFlags", redFlag, "none",
                "Pre-computed signal raised by the extraction stage.",
                "See Extraction.Issues and the file-facts report for context."));
    }

    private static void EvaluateChunking(ChunkingStageMetrics? c, List<ReportFlag> flags, bool calibrationMode)
    {
        if (c is null) return;

        if (c.DocsWithZeroChunks > 0)
        {
            var names = c.ZeroChunkDocumentIds.Count > 0
                ? string.Join(", ", c.ZeroChunkDocumentIds.Take(5))
                : "(ids unavailable)";
            flags.Add(new ReportFlag(FlagSeverity.Critical, "Chunking.DocsWithZeroChunks",
                $"{c.DocsWithZeroChunks} ({names})", "0",
                "These documents produced no chunks and are absent from the index — unsearchable.",
                "Check whether their content was empty after cleaning, or extraction failed for them."));
        }

        // Sector disambiguation missing on the documents that need it (2026-08-27). Sourced, not
        // calibrated: there is no rate to tune - a document inside a multi-member family with no
        // DomainTag means the near-duplicate set it belongs to cannot be told apart, and one is
        // already wrong. This is the rule that was missing when 2 of 3 CAO documents silently lost
        // their tag under Content Understanding; the per-document DomainTag was in the chunking
        // artifact the whole time and nothing evaluated it, so it surfaced as an eval regression
        // instead. See docs/2608/260827/extraction-coverage-chunking-review.md Gap 2.
        if (c.UntaggedFamilyMemberIds.Count > 0)
            flags.Add(new ReportFlag(FlagSeverity.Warning, "Chunking.UntaggedFamilyMemberIds",
                $"{c.UntaggedFamilyMemberIds.Count} ({string.Join(", ", c.UntaggedFamilyMemberIds.Take(5))})", "0",
                "These documents sit in a multi-member family but carry no DomainTag — the near-duplicate set they belong to cannot be disambiguated by sector, so retrieval can answer from the wrong one.",
                "Check IdentityTagger in the run logs: either the DomainClassifier call failed for these documents (retried automatically next run) or the model judged that no population applies — for a multi-member family the latter is worth a human look."));

        // Identity text is title + every heading, embedded as ONE uncapped input - the only place
        // the model's per-input limit is live, and the failure past it is a silent truncation of
        // the document's structure (IdentityTokenMetrics). Sourced, not calibrated: the limit is
        // the model's, the 80% line is DocumentIdentityBuilder's own tripwire, carried on the
        // record. Null = the report predates the field (2026-09-15). Before this flag the tripwire
        // wrote only into the chunking artifact and its crossing on 260909/1 was found by hand.
        if (c.IdentityTokens is { } it)
        {
            if (it.Max > it.Limit)
                flags.Add(new ReportFlag(FlagSeverity.Critical, "Chunking.IdentityTokens.Max",
                    $"{it.Max} tokens", $"≤ {it.Limit}",
                    "At least one document's identity text is over the model's per-input limit — the end of its heading list did not reach the vector its family is clustered on, and nothing else reports that.",
                    "It is first in IdentityTokens.NearingLimit. Cap, sample or summarise its heading list — that changes every identity hash and forces a full re-cluster, so do it deliberately (D191 §4, D192 §5)."));
            else if (it.NearingLimitCount > 0)
            {
                var names = string.Join(", ", it.NearingLimit.Take(5).Select(d => $"{d.SourceId} ({d.Tokens})"));
                var pct   = it.Limit > 0 ? it.WarningThreshold / (double)it.Limit : 0;
                flags.Add(new ReportFlag(FlagSeverity.Warning, "Chunking.IdentityTokens.NearingLimit",
                    $"{it.NearingLimitCount} ({names})", $"none over {it.WarningThreshold} tokens ({pct:P0} of {it.Limit})",
                    $"These documents' identity text is within {1 - pct:P0} of the per-input limit; headroom on the largest is {it.Limit - it.Max} tokens. Past the limit the tail of the heading list is dropped silently.",
                    "Decide the cap before it is forced: capping, sampling or summarising the heading list changes every identity hash and forces a full re-cluster (D191 §4, D192 §5)."));
            }
        }

        if (c.ChunksProduced == 0) return; // nothing to compute ratios against

        var undersized = c.BandUnder100 / (double)c.ChunksProduced;
        if (undersized > UndersizedWarnRate)
            flags.Add(new ReportFlag(
                undersized > UndersizedCriticalRate ? FlagSeverity.Critical : FlagSeverity.Warning,
                "Chunking.BandUnder100",
                $"{c.BandUnder100} ({undersized:P0})", $"≤ {UndersizedWarnRate:P0}",
                "Fragments too short to carry retrievable meaning.",
                "Inspect the smallest-chunk sample; usually a split on a stray heading or table boundary.")
            { AwaitingCalibration = true });

        var oversized = c.Band1500Plus / (double)c.ChunksProduced;
        if (oversized > OversizedWarnRate)
            flags.Add(new ReportFlag(FlagSeverity.Warning, "Chunking.Band1500Plus",
                $"{c.Band1500Plus} ({oversized:P0})", $"≤ {OversizedWarnRate:P0}",
                "Large chunks dilute the embedding — retrieval precision drops.",
                "Inspect the largest-chunk sample.")
            { AwaitingCalibration = true });

        var duplicates = c.DuplicateChunks / (double)c.ChunksProduced;
        if (duplicates > DuplicateWarnRate)
            flags.Add(new ReportFlag(FlagSeverity.Warning, "Chunking.DuplicateChunks",
                $"{c.DuplicateChunks} ({duplicates:P0})", $"≤ {DuplicateWarnRate:P0}",
                "Identical content indexed more than once — wasted vector space and duplicate hits.",
                "See DuplicateSamples for the repeated text; usually boilerplate headers/footers.")
            { AwaitingCalibration = true });
    }

    // The live index definition against configuration (2026-09-15). EnsureIndexAsync is
    // get-or-create, so a configuration change after the index exists never reaches it: these
    // are the three disagreements that produce wrong results without an error anywhere else.
    // Sourced - equalities, and the model vendor's own documentation for the metric - so none
    // await calibration. Null = the read failed or the report predates the field: no flags.
    private static void EvaluateVectorConfig(AgenticRagApp.Infrastructure.Clients.Search.IndexVectorConfig? v, List<ReportFlag> flags)
    {
        if (v is null) return;

        if (!v.FieldPresent)
        {
            flags.Add(new ReportFlag(FlagSeverity.Critical, "Index.VectorField",
                "absent", v.FieldName,
                "The live index has no field by the name the code uploads vectors into — every upload fails on it.",
                "The index was created from a different schema; recreate it (RecreateIndexAsync) and re-embed."));
            return;
        }

        // Warning since 2026-09-17 (D201), and the meaning changed with it.
        //
        // It used to be Critical because vectors were validated against OPENAI_EMBEDDING_DIMENSIONS,
        // so a config that disagreed with the index meant the next upload was already doomed - this
        // comment used to read "VectorDimErrors cannot see this one: it compares each vector against
        // the same configuration value, so a config change without a re-index passes it and fails at
        // upload." That sentence is what prompted D201: validation now happens against the live
        // index width, read at preflight.
        //
        // So this no longer predicts a failure. It says the configured value would provision a
        // DIFFERENT index the next time one is created - a latent footgun on the next recreate,
        // not a broken run. Nothing is failing now, which is what makes it a Warning.
        if (v.Dimensions is { } dims && dims != v.ConfiguredDimensions)
            flags.Add(new ReportFlag(FlagSeverity.Warning, "Index.VectorDimensions",
                $"{dims} (index field)", $"{v.ConfiguredDimensions} (OPENAI_EMBEDDING_DIMENSIONS)",
                "The live index is not the width the configuration asks for. This run was unaffected — vectors are validated against the index, not against configuration — but the next index creation would build it at the configured width, and the daily run recreates the index.",
                "Decide which is right before the next recreate: set OPENAI_EMBEDDING_DIMENSIONS to the width the index actually has, or keep it and accept that the next recreate rebuilds the index at that width, which requires re-embedding the corpus."));

        // The vectorizer embeds QUERIES; the documents were embedded by the configured model. Two
        // different models produce vectors that compare, but not meaningfully - nothing errors.
        if (v.VectorizerModel is { } model && !string.Equals(model, v.ConfiguredModelName, StringComparison.OrdinalIgnoreCase))
            flags.Add(new ReportFlag(FlagSeverity.Critical, "Index.VectorizerModel",
                model, v.ConfiguredModelName,
                "Queries are embedded by a different model than the documents were — retrieval degrades silently, with no error on either side.",
                "The vectorizer is fixed at index creation: recreate the index with the configured model, or set OPENAI_EMBEDDING_MODEL back to the one the index was built with and re-embed if the documents changed."));

        // text-embedding-3 returns unit-length vectors and its vendor documents cosine; the code
        // sets no metric, so what is here is the service default - worth knowing if it ever is not.
        if (v.Metric is { } metric && !string.Equals(metric, "cosine", StringComparison.OrdinalIgnoreCase))
            flags.Add(new ReportFlag(FlagSeverity.Warning, "Index.VectorMetric",
                metric, "cosine",
                "The index compares vectors with a metric other than the one the embedding model is documented for; rankings differ for no gain.",
                "BuildVectorSearch sets no metric, so this was set outside the code; recreating the index is the only way to change it."));
    }

    private static void EvaluateEmbedding(EmbedUploadStageMetrics? e, ChunkingStageMetrics? c, IndexStatsReadback? readback, List<ReportFlag> flags)
    {
        if (e is null) return;

        // DocsFailed folds two causes with the same consequence and opposite remedies: chunks
        // Azure AI Search refused, and chunks this pipeline withheld because their vector failed
        // VectorHealth.Classify (2026-09-17, D199 A1). DocsWithheld carries the split explicitly -
        // read, not inferred from VectorDimErrors + EmptyVectors, which happens to equal it today
        // but is not enforced to. Null on reports that predate the field, which then read exactly
        // as they did before.
        if (e.DocsFailed > 0)
        {
            var withheld = e.DocsWithheld ?? 0;
            var refused  = e.DocsFailed - withheld;

            // "Silently" is true only of the refused half. A withheld chunk is logged at Error
            // with its id and verdict, plus a per-verdict summary line.
            var consequence = withheld == 0
                ? "Those chunks are silently missing from the index."
                : $"{refused} refused by Search (silently missing); {withheld} withheld by the pipeline and logged with their chunk ids. " +
                  "A withheld chunk that replaced an existing row leaves the previous content live and the document reading stale, " +
                  "so it returns on the next run by itself; one that never had a row is simply absent and will not come back on its own.";

            // Upstream first, deliberately: re-running before the cause is fixed spends a
            // Content Understanding extraction to reach the same verdict. The report cannot say
            // which withheld chunks had a prior row, so "the documents named in the log" is the
            // instruction that is safe either way.
            var remediation = withheld == 0
                ? "Re-run indexing for the affected documents; check Search service throttling."
                : "Fix the cause before re-running. The host log's first 20 withheld ids and its per-verdict summary say which: " +
                  "wrong width means OPENAI_EMBEDDING_DIMENSIONS has drifted from the index's content_vector field, anything else means the embedding deployment. " +
                  "Then re-run indexing for the documents named in that log. For the refused chunks, check Search service throttling.";

            flags.Add(new ReportFlag(FlagSeverity.Critical, "Embedding.DocsFailed",
                e.DocsFailed.ToString(), "0", consequence, remediation));
        }

        if (e.VectorDimErrors > 0)
            flags.Add(new ReportFlag(FlagSeverity.Critical, "Embedding.VectorDimErrors",
                e.VectorDimErrors.ToString(), "0",
                "A model/config dimension mismatch — vectors don't match the index schema.",
                "Check OPENAI_EMBEDDING_DEPLOYMENT and the index's vector dimensions agree."));

        // Null on reports that predate the counter (2026-09-15): not measured, no flag.
        if (e.EmptyVectors > 0)
            flags.Add(new ReportFlag(FlagSeverity.Critical, "Embedding.EmptyVectors",
                e.EmptyVectors.Value.ToString(), "0",
                "Right-length vectors that are all-zero or non-finite — indexed, but they can never match a query.",
                "Find the chunk ids in the run's host log (\"Empty vector\"), check the deployment's responses for them, and re-index the affected documents."));

        if (e.ChunksTruncated > 0)
            flags.Add(new ReportFlag(FlagSeverity.Warning, "Embedding.ChunksTruncated",
                e.ChunksTruncated.ToString(), "0",
                "Embedded with incomplete content — cut at the 24k-char pre-filter or the 8,191-token input limit before embedding.",
                "These chunks retrieve on partial semantics; consider splitting them earlier."));

        // Every chunk the chunker emitted goes to the uploader, and the uploader accounts for
        // each one as succeeded or failed - so the two sides must add up. DocsFailed already
        // has its own flag above; this one catches the case with NO error: chunks that went
        // missing between the chunker and the index, or were counted twice. An identity, not
        // a threshold, so nothing here awaits calibration.
        if (c is not null && e.DocsUploaded + e.DocsFailed != c.ChunksProduced)
            flags.Add(new ReportFlag(FlagSeverity.Critical, "Embedding.DocsUploaded",
                $"{e.DocsUploaded} uploaded + {e.DocsFailed} failed", $"{c.ChunksProduced} (Chunking.ChunksProduced)",
                "The uploader accounted for a different number of chunks than the chunker produced — chunks were lost or double-counted without an upload error.",
                "Diff the chunk ids in the chunking artifact against the embedding artifact for this run; a shortfall with DocsFailed = 0 is a pipeline bug, not a Search problem."));

        if (c is { ChunksProduced: > 0 })
        {
            var retryRate = e.EmbeddingRetries / (double)c.ChunksProduced;
            if (retryRate > EmbeddingRetryWarnRate)
                flags.Add(new ReportFlag(
                    retryRate > EmbeddingRetryCriticalRate ? FlagSeverity.Critical : FlagSeverity.Warning,
                    "Embedding.EmbeddingRetries",
                    $"{e.EmbeddingRetries} ({retryRate:P1} of chunks)", $"≤ {EmbeddingRetryWarnRate:P0}",
                    "OpenAI rate limits are being hit — embedding is running degraded.",
                    "Raise the deployment's TPM quota or lower embedding concurrency."));
        }

        // Drift. Uses the baseline carried on the stage record rather than re-reading
        // _last-stats-{source}.json, which by now holds this run's own numbers.
        //
        // Prefers the report-time readback over the post-upload snapshot, and requires a
        // nonzero count: the snapshot regularly reads 0 because Azure Search stats lag the
        // writes that just happened, and comparing that zero produced a Critical
        // "-100% from 2,000" on runs 260826/c546ab8f and /a582e6c4 whose corpus had not
        // changed. IndexStatsMonitor already declines to compare a zero for exactly this
        // reason - this is the same guard, in the half of the pipeline that missed it.
        // Without the preference, run a582e6c4 flagged deletion while its OWN
        // snapshot_unverified check stood down on a readback of 3,878.
        var observed = readback is { DocumentCount: > 0 } r ? r.DocumentCount : e.IndexDocumentCountSnapshot;
        if (e is { PreviousIndexDocumentCount: > 0 } && observed is > 0)
        {
            var prev  = e.PreviousIndexDocumentCount.Value;
            var now   = observed.Value;
            var delta = (now - prev) / (double)prev;

            if (Math.Abs(delta) > DriftWarnPct)
                flags.Add(new ReportFlag(
                    Math.Abs(delta) > DriftCriticalPct ? FlagSeverity.Critical : FlagSeverity.Warning,
                    "Embedding.IndexDocumentCountSnapshot",
                    $"{now:N0} ({delta:+0.0%;-0.0%} from {prev:N0})", $"within ±{DriftWarnPct:P0}",
                    "The index size moved more than the corpus should between runs.",
                    "Confirm against DocsUploaded/ChunksRemoved — a large drop with a small run is a deletion bug."));
        }
    }

    // Run-level health signals read off the report envelope rather than one stage's record
    // (observability plan 4.2). The duration flags carry the 260825 watch item as far as a
    // report can: a run that never completes writes no report, so a hung run stays a manual
    // GET /api/index/status check - stated in the plan as the accepted limit of reports-only.
    private static void EvaluateRunHealth(PdfIndexRunReport report, List<ReportFlag> flags)
    {
        // extract_duration_high. Durations exist only for stages that ran to completion.
        if (report.StageDurationsMs?.TryGetValue("extract", out var extractMs) == true)
        {
            var extractDuration = TimeSpan.FromMilliseconds(extractMs);
            if (extractDuration > ExtractDurationWarn)
                flags.Add(new ReportFlag(
                    extractDuration > ExtractDurationCritical ? FlagSeverity.Critical : FlagSeverity.Warning,
                    "Run.ExtractDuration",
                    $"{extractDuration.TotalMinutes:F0} min", $"≤ {ExtractDurationWarn.TotalMinutes:F0} min",
                    "Extraction ran into the wall-clock/activity-timeout danger zone — past ~70 min Durable redelivers and re-bills the whole activity.",
                    "Check the file-facts DurationMs column for which documents took the time; a run near the limit should be terminated, not left to redeliver (260825 watch item)."));
        }

        // snapshot_unverified: the run uploaded, and neither stats sample confirms anything
        // landed. Both reads lagging is common minutes-scale behavior for Azure Search stats;
        // the flag says "unverified", not "failed".
        var e = report.Embedding;
        if (e is { DocsUploaded: > 0 }
            && e.IndexDocumentCountSnapshot is null or 0
            && report.StatsReadback is null or { DocumentCount: 0 })
            flags.Add(new ReportFlag(FlagSeverity.Watch, "Run.SnapshotUnverified",
                "post-upload snapshot and report-time readback both empty", "a document count",
                "Upload success is self-reported only — nothing independently confirmed the documents landed.",
                "Check the next run's PreviousIndexDocumentCount, or query the index's $count directly."));
    }

    // The Validation.* flags that lived here are gone with the validation report they read
    // (2026-09-09). Three flags, none of them reachable: TableConversionFallbacks and
    // DocumentsNeedingFallbackChunking were PdfCleaner/PdfPipelineValidator counters that no
    // stage in the CU pipeline produces, and MagnitudeWarnings needed a baseline comparison
    // that never got ported. The magnitude check is the one worth having back - the baseline
    // exists already (PreviousRunPointer, plus the page count SaveRunStateAsync writes every
    // run), so it belongs here against `previous` rather than in a report of its own. It is
    // NOT written yet: the only measurement of run-to-run variance so far is a ±0.7%
    // chunk-count band over three runs (docs/2608/260827/todays-runs-comparison.md), which is
    // a noise floor and not a warn threshold.

    private static void EvaluateCost(
        FileFactsSummary? facts, PreviousRunPointer? previous, List<ReportFlag> flags, bool calibrationMode)
    {
        if (facts is null || facts.EstimatedCostUsd <= 0) return;

        // No previous cost is carried on the pointer yet, so this can only fire once a baseline
        // exists. Left in place rather than removed: it is the only spend signal in the pipeline.
        _ = previous;
        _ = CostMultiplierWarn;
    }
}
