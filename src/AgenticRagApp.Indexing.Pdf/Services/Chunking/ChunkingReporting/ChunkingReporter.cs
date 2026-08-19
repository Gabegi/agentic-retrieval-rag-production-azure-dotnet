using Microsoft.Extensions.Logging;
using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Step 5: everything the chunking stage says about itself - the log lines, the OpenTelemetry
// counters and the chunking-artifact blob - from one call, made once, from ChunkDocumentsAsync's
// finally.
//
// From a finally, so a run that throws part way through still produces a report marked with the
// stage it died in. That is the whole point: the failure modes worth diagnosing (a
// wrong-dimension vector, duplicate SourceIds) throw out of identity resolution, and before this
// the activity's artifact was written only on the success path - so the runs most in need of a
// report were exactly the ones without one.
//
// Not in ChunkActivity, for two structural reasons: the activity's catch rethrows
// (PdfIndexingFunction.cs), so anything written there lands on the success path only; and the
// activity does not know the contents - identity diagnostics, per-document route, heading
// location and residue drops exist only inside the stage.
public sealed class ChunkingReporter
{
    // Above this share of headings failing to locate, the string-match approach is no longer
    // holding and the run says so out loud. Fixed IN ADVANCE against a measured 1,273/1,273
    // exact-match rate, which is why the rate is reported every run rather than measured once
    // and assumed to hold.
    private const double HeadingEscalationThreshold = 0.02;

    // The per-document half of the same rule, fixed in advance alongside it. Looser than the
    // corpus figure on purpose: one document is a small sample, so a single unlocated heading in
    // twenty is noise where the same rate across 1,273 is a broken approach. A corpus that
    // locates 1,270 of 1,273 passes the >2% test comfortably while one small document sits at
    // 100% unlocated - which is exactly the document whose chunks are all preamble, and exactly
    // what a run total cannot show.
    private const double PerDocumentHeadingEscalationThreshold = 0.05;

    // The pre-existing artifact's name, kept so the blob a reader already knows how to find is
    // the one that now carries the whole stage rather than just the chunk list.
    private const string ChunkingReportName = "chunking-artifact";

    private readonly IPipelineArtifactWriter    _artifactWriter;
    private readonly ILogger<ChunkingReporter>  _logger;

    public ChunkingReporter(IPipelineArtifactWriter artifactWriter, ILogger<ChunkingReporter> logger)
    {
        _artifactWriter = artifactWriter;
        _logger         = logger;
    }

    // Never lets a reporting failure mask the stage's own outcome - on the success path it would
    // fail a run that worked, and on the failure path it would replace the real exception with a
    // blob-write error. That is also why the logging sits inside the same guard: a formatting
    // slip in a log line is not worth a failed run.
    public async Task WriteAsync(ChunkingRunState state, CancellationToken ct)
    {
        try
        {
            LogDocuments(state);

            var headingSummary = BuildHeadingSummary(state);

            LogRun(state);

            // Null when the stage threw before metrics ran. The report still goes out; the
            // counters have nothing to report yet.
            if (state.Stats is not null)
                ChunkMetricsEmitter.Emit(state.Stats, state.Chunks);

            await _artifactWriter.WriteArtifactAsync(
                StageReportPath.Build(ChunkingReportName, state.StartedAt, state.InstanceId),
                new ChunkingRunReport(
                    InstanceId:      state.InstanceId,
                    StartedAt:       state.StartedAt,
                    CompletedAt:     DateTimeOffset.UtcNow,
                    Success:         state.Error is null,
                    FailedAtStage:   state.Error is null ? null : state.Stage,
                    Error:           state.Error,
                    Documents:       BuildRows(state),
                    Identity:        state.Identity,
                    HeadingLocation: headingSummary,
                    Stats:           state.Stats,
                    // Uncapped by design - the writer streams, so this never buffers whole. What
                    // is NOT yet known is what it now weighs: every ChunkObject carries
                    // Metadata.Structure since step 4, where the flat type it replaced carried
                    // far less, and 3,046 chunks is the count that makes that matter.
                    //
                    // MEASURE THIS ON THE FIRST REAL RUN: the size of the chunking-artifact blob.
                    // If it is uncomfortable, the cheapest cut is to project Structure out of the
                    // chunks written HERE only - it is stamped per chunk, excluded from the
                    // snapshot already, and recomputable - rather than capping the list, which
                    // would remove the corpus-wide view the artifact exists to give.
                    // chunking-done.md §12 found-3, §17 item 9.
                    Chunks:          state.Chunks),
                ct);
        }
        catch (Exception reportEx)
        {
            _logger.LogError(reportEx, "Failed to write the chunking run report");
        }
    }

    // Every input document, one row each, ordered by SourceId. Documents the loop never reached
    // are derived rather than tracked - the stage threw before their turn, so there is nothing
    // to have recorded about them beyond the fact itself.
    private static IReadOnlyList<DocumentOutcome> BuildRows(ChunkingRunState state)
    {
        var notReachedReason = state.Error is null
            ? null
            : $"the chunking stage failed at '{state.Stage}' before this document was processed";

        return state.Docs
            .Select(doc => DocumentRowBuilder.Build(
                doc,
                state.FactsOrNull(doc.SourceId),
                state.FamilyOf(doc.SourceId),
                state.VectorSourceOf(doc.SourceId),
                state.IsInMultiMemberFamily(doc.SourceId),
                notReachedReason))
            .OrderBy(o => o.SourceId, StringComparer.Ordinal)
            .ToList();
    }

    // The standing evidence for locating headings by string match rather than rewriting
    // PdfCleaner to emit an offset map.
    //
    // Zero total means no document took the declared-boundary route this run, so there is
    // nothing to report - not a 0% failure rate, no attempt.
    private HeadingLocationSummary? BuildHeadingSummary(ChunkingRunState state)
    {
        if (state.HeadingsTotal == 0) return null;

        var failureRate = 1 - (state.HeadingsLocated / (double)state.HeadingsTotal);
        var exceeds     = failureRate > HeadingEscalationThreshold;

        _logger.Log(exceeds ? LogLevel.Warning : LogLevel.Information,
            "Heading location: {Found}/{Total} ({Rate:P2} unlocated), {Merged} paired zero-body heading(s) merged",
            state.HeadingsLocated, state.HeadingsTotal, failureRate, state.PairedHeadingsMerged);

        return new HeadingLocationSummary(
            state.HeadingsTotal, state.HeadingsLocated, failureRate, exceeds,
            state.PairedHeadingsMerged, state.HeadingsWithoutOffset);
    }

    // The per-document lines. Emitted here, at the end of the run, rather than inside the loop:
    // the loop is the algorithm, and every one of these reads a fact the state already carries.
    // The cost is ordering - they no longer interleave with whatever else logged mid-run - and
    // the gain is that the SourceId is on every line either way.
    private void LogDocuments(ChunkingRunState state)
    {
        foreach (var facts in state.DocumentFacts)
        {
            // A heading whose paragraph carried no DI spans at all, so nothing said where in the
            // raw content it sits. HeadingLocator kept it with its neighbours by carrying the
            // previous offset forward, which is the best available answer but still a fallback -
            // the section boundary it opens now rests on arrival order. Zero of 1,273 headings
            // across the big four did this, so it is an extraction anomaly worth chasing
            // upstream, not a routine input.
            if (facts.HeadingsWithoutOffset > 0)
                _logger.LogWarning(
                    "{Count} of {Total} headings in {SourceId} carried no DI offset and were ordered " +
                    "by arrival position instead",
                    facts.HeadingsWithoutOffset, facts.HeadingsTotal, facts.SourceId);

            // The other half of the escalation rule: >2% corpus-wide OR >5% on any single
            // document. Only the corpus-wide branch ever had code.
            if (facts.HeadingsTotal > 0)
            {
                var rate = 1 - (facts.HeadingsLocated / (double)facts.HeadingsTotal);

                if (rate > PerDocumentHeadingEscalationThreshold)
                    _logger.LogWarning(
                        "Heading location in {SourceId}: {Found}/{Total} ({Rate:P2} unlocated), over the " +
                        ">{Threshold:P0} per-document escalation threshold",
                        facts.SourceId, facts.HeadingsLocated, facts.HeadingsTotal, rate,
                        PerDocumentHeadingEscalationThreshold);
            }

            if (facts.ResidueDropped > 0)
                _logger.LogInformation(
                    "Minimum-content rule dropped {Dropped} of {Total} cut(s) as vector residue in {SourceId}",
                    facts.ResidueDropped, facts.CutCount, facts.SourceId);

            // The stage fails on this too, once, at the end - but the exception it throws names
            // the documents and not the failures, so the failures are logged whole here.
            if (facts.Exception is not null)
                _logger.LogError(facts.Exception,
                    "Chunking failed for {SourceId}; the remaining documents were still processed",
                    facts.SourceId);
        }
    }

    // Two route counts rather than a strategy name: the service's own Name is "TwoAxisChunking"
    // whichever way each document went, so logging it said nothing once there were two routes.
    private void LogRun(ChunkingRunState state)
    {
        var declared  = state.DocumentFacts.Count(f => f.Route == RouteNames.DeclaredBoundary);
        var recursive = state.DocumentFacts.Count(f => f.Route == RouteNames.Recursive);

        _logger.LogInformation(
            "Chunked {Docs} docs into {Chunks} chunks ({Declared} declared-boundary, {Recursive} recursive)",
            state.Docs.Count, state.Chunks.Count, declared, recursive);

        // The run total, so a corpus that sheds residue across many documents says so once
        // rather than only a line at a time. Non-zero is normal on image-heavy documents; a
        // sharp rise is an extraction change, not a chunking one.
        if (state.ResidueDropped > 0)
            _logger.LogInformation(
                "Minimum-content rule dropped {Dropped} cut(s) as vector residue across the run",
                state.ResidueDropped);
    }
}
