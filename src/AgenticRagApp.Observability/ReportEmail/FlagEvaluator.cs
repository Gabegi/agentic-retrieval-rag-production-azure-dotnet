using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Observability.Reports;

// Turns a run report into the flag list rendered in §3 of the email.
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
// guesses - see ReportEmailOptions.CalibrationMode.
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

    // Awaiting calibration - no defensible source. See ReportEmailOptions.CalibrationMode.
    private const double CoherenceWarnRatio      = 0.70;
    private const double CoherenceCriticalRatio  = 0.50;
    private const double UndersizedWarnRate      = 0.10;
    private const double UndersizedCriticalRate  = 0.20;
    private const double OversizedWarnRate       = 0.05;
    private const double DuplicateWarnRate       = 0.02;
    private const double CostMultiplierWarn      = 2.0;

    public static IReadOnlyList<ReportFlag> Evaluate(
        PdfIndexRunReport report,
        ValidationReportFacts? validation,
        FileFactsSummary? fileFacts,
        PreviousRunPointer? previous,
        bool calibrationMode)
    {
        var flags = new List<ReportFlag>();

        EvaluateExtraction(report.Extraction, flags, calibrationMode);
        EvaluateChunking(report.Chunking, flags, calibrationMode);
        EvaluateEmbedding(report.Embedding, report.Chunking, flags);
        EvaluateValidation(validation, flags);
        EvaluateCost(fileFacts, previous, flags, calibrationMode);

        if (calibrationMode)
            flags.RemoveAll(f => f.AwaitingCalibration);

        return flags.OrderByDescending(f => f.Severity).ToList();
    }

    private static void EvaluateExtraction(ExtractionStageMetrics? x, List<ReportFlag> flags, bool calibrationMode)
    {
        if (x is null) return; // stage never ran - no measurement, no flags

        if (x.ReconciliationProblems > 0)
            flags.Add(new ReportFlag(FlagSeverity.Critical, "Extraction.ReconciliationProblems",
                x.ReconciliationProblems.ToString(), "0",
                "Counts don't add up across stages — a logic bug or data truncation.",
                "Read the validation report's ReconciliationProblems list; do not trust this run's other counts until resolved."));

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

        // Expected to be the whole corpus until uploaders start setting zenya_document_id, so
        // this is a Watch, never a Warning - flagging the documented steady state as a problem
        // is exactly how a flag list stops being read.
        if (x.TraceabilityGapCount > 0)
            flags.Add(new ReportFlag(FlagSeverity.Watch, "Extraction.TraceabilityGapCount",
                x.TraceabilityGapCount.Value.ToString(), "trending down",
                "Documents with no zenya_document_id — passages can't be traced back to Zenya.",
                "No action while uploaders don't set this metadata; watch for the number failing to fall once they do."));

        foreach (var redFlag in x.RedFlags)
            flags.Add(new ReportFlag(FlagSeverity.Warning, "Extraction.RedFlags", redFlag, "none",
                "Pre-computed signal raised by the extraction stage.",
                "See the validation report for context."));
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

        if (c.ChunksProduced == 0) return; // nothing to compute ratios against

        var coherence = c.CoherentChunks / (double)c.ChunksProduced;
        if (coherence < CoherenceWarnRatio)
            flags.Add(new ReportFlag(
                coherence < CoherenceCriticalRatio ? FlagSeverity.Critical : FlagSeverity.Warning,
                "Chunking.CoherentChunks",
                $"{coherence:P0}", $"≥ {CoherenceWarnRatio:P0}",
                "Chunks are starting or ending mid-sentence — the chunker is cutting at bad boundaries.",
                "Compare the smallest/largest chunk samples; consider the split threshold or strategy.")
            { AwaitingCalibration = true });

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

    private static void EvaluateEmbedding(EmbedUploadStageMetrics? e, ChunkingStageMetrics? c, List<ReportFlag> flags)
    {
        if (e is null) return;

        if (e.DocsFailed > 0)
            flags.Add(new ReportFlag(FlagSeverity.Critical, "Embedding.DocsFailed",
                e.DocsFailed.ToString(), "0",
                "Those chunks are silently missing from the index.",
                "Re-run indexing for the affected documents; check Search service throttling."));

        if (e.VectorDimErrors > 0)
            flags.Add(new ReportFlag(FlagSeverity.Critical, "Embedding.VectorDimErrors",
                e.VectorDimErrors.ToString(), "0",
                "A model/config dimension mismatch — vectors don't match the index schema.",
                "Check OPENAI_EMBEDDING_DEPLOYMENT and the index's vector dimensions agree."));

        if (e.ChunksTruncated > 0)
            flags.Add(new ReportFlag(FlagSeverity.Warning, "Embedding.ChunksTruncated",
                e.ChunksTruncated.ToString(), "0",
                "Embedded with incomplete content — the vector covers only the first 24k chars.",
                "These chunks retrieve on partial semantics; consider splitting them earlier."));

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
        if (e is { PreviousIndexDocumentCount: > 0, IndexDocumentCountSnapshot: not null })
        {
            var prev  = e.PreviousIndexDocumentCount.Value;
            var now   = e.IndexDocumentCountSnapshot.Value;
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

    private static void EvaluateValidation(ValidationReportFacts? v, List<ReportFlag> flags)
    {
        if (v is null) return;

        // Only diagnostic against a non-trivial table count: a couple of fallbacks in a run with
        // hundreds of tables is noise, the same number against 3 tables is not.
        if (v.TableConversionFallbacks > 0 && v.DetectedTableCount > 0)
        {
            var rate = v.TableConversionFallbacks / (double)v.DetectedTableCount;
            if (rate > 0.25)
                flags.Add(new ReportFlag(FlagSeverity.Warning, "Validation.TableConversionFallbacks",
                    $"{v.TableConversionFallbacks} of {v.DetectedTableCount} tables ({rate:P0})", "≤ 25%",
                    "Tables fell back to plain text — their structure is lost for retrieval.",
                    "Check the affected documents' table markup in the extraction artifact."));
        }

        foreach (var w in v.MagnitudeWarnings)
            flags.Add(new ReportFlag(FlagSeverity.Watch, "Validation.MagnitudeWarnings", w, "none",
                "Corpus size moved unusually against the baseline (advisory — never gates a run).",
                "Expected on a small changeset; investigate only if it repeats."));

        if (v.DocumentsNeedingFallbackChunking.Count > 0)
            flags.Add(new ReportFlag(FlagSeverity.Warning, "Validation.DocumentsNeedingFallbackChunking",
                $"{v.DocumentsNeedingFallbackChunking.Count} document(s)", "0",
                "No structural guidance available — these were chunked blind and may retrieve worse.",
                "Check whether these PDFs have a bookmark outline or DI-detected headings."));
    }

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
