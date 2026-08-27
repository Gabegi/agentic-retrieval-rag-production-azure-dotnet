using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Observability;
using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Indexing.CU.Services;

// Assembles what the extraction stage RETURNS - the documents handed to the chunking stage and
// the ExtractionStageMetrics row that travels with them - as opposed to what it EMITS, which is
// ExtractionReporter's job. Both moved out of ExtractionService together, but they are kept apart
// here on purpose: this half is pure (no logger, no writer, no counters), so it is testable by
// calling it.
//
// The three steps below are one job and are kept in one place for that reason: decide which
// documents may be torn down, assemble the DiffResult, derive the metrics row from it.
// ExtractionService's tail is a single call into BuildResult.
internal static class ExtractionStatsBuilder
{
    // SearchDocumentStore.GetCurrentIndexedDocumentDatesAsync =
    // Give me a list of all distinct source_ids in the index along with their recorded timestamp."

    // Tripwire for finding #4 (SearchDocumentStore.GetCurrentIndexedDocumentDatesAsync
    // truncating the indexed-dates read): if the index is non-empty and this isn't a forced
    // reindex, but most of the corpus still reads as "new", that's not a growing corpus -
    // it's the signature of the diff's target side coming back incomplete. A brand-new
    // index (indexedCount == 0) is excluded on purpose: everything being new on the very
    // first run is expected, not a symptom of anything.
    private const double HighNewDocFractionThreshold = 0.5;

    // The whole return value of the extraction stage, from the run's diff and its output.
    // extraRedFlag: a caller-supplied line for the metrics row's red flags (currently the CU
    // model-defaults state from host startup - see ExtractionService's comment at the call).
    internal static (IReadOnlyList<PdfExtractionDocument> Docs, ExtractionStageMetrics Stats) BuildResult(
        string source, IndexDiff diff, PdfExtractionOutput output, bool forceReindex,
        string? extraRedFlag = null)
    {
        var result = new DiffResult(
            source, output, output.Docs.ToList(), diff.RemovedSourceIds.ToList(),
            StaleDocumentIds(diff, output), diff.NewCount, diff.Updated, diff.Skipped);

        IReadOnlyList<string> extras =
        [
            .. HighNewDocFractionRedFlag(diff.SourceCount, diff.IndexedCount, diff.NewCount, forceReindex),
            .. extraRedFlag is null ? Array.Empty<string>() : [extraRedFlag],
        ];

        return (result.ToProcess, BuildStats(result, extras));
    }

    // A document slated for update (EntriesToProcess) is only safe to mark stale if this run's
    // extraction actually produced replacement content for it. Extraction failures don't fail the
    // whole run on their own, and the corpus wall-clock guard stops submitting files without
    // recording an error at all - without this filter, either one on an updated document would
    // still reach UploadService as "stale," which deletes every existing chunk for it with nothing
    // to replace them, silently dropping that document from the index. Docs staged for deletion
    // because they're removed from the source were never added to
    // EntriesToProcess, so they're unaffected by this filter - there's no replacement to wait for;
    // they're actually gone from the source.
    //
    // ExtractionServiceTests covers both directions: the updated-but-not-extracted document that
    // must NOT be torn down, and the removed one that must be torn down anyway.
    private static List<string> StaleDocumentIds(IndexDiff diff, PdfExtractionOutput output)
    {
        var extractedSourceIds = output.Docs
            .Select(d => d.SourceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return [.. diff.ToDeleteChunks
            .Where(id => !diff.EntriesToProcess.ContainsKey(id) || extractedSourceIds.Contains(id))];
    }

    internal static IReadOnlyList<string> HighNewDocFractionRedFlag(
        int sourceCount, int indexedCount, int newCount, bool forceReindex)
    {
        if (forceReindex || indexedCount == 0 || sourceCount == 0) return [];

        var newFraction = (double)newCount / sourceCount;
        if (newFraction < HighNewDocFractionThreshold) return [];

        return [$"high_new_doc_fraction:{newFraction:P0} ({newCount}/{sourceCount}) despite {indexedCount} already-indexed document(s) - possible truncated index-state read"];
    }

    // Assemble ExtractionStageMetrics to return to the activity
    internal static ExtractionStageMetrics BuildStats(DiffResult diff, IReadOnlyList<string> extraRedFlags) => new(
        Source:                 diff.Source,
        // diff.ToProcess is page-grained, same as diff.Output.Docs above - distinct SourceIds
        // gives the document count report-schema.md documents this field as (= DocsNew + DocsUpdated).
        DocsToProcess:          diff.ToProcess.Select(d => d.SourceId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
        DocsSkipped:            diff.Skipped,
        DocsNew:                diff.NewCount,
        DocsUpdated:            diff.Updated,
        DocsDeleted:            diff.RemovedSourceIds.Count,
        StaleDocumentIds:       diff.StaleDocumentIds,
        ValidationErrors:       diff.Output.ValidationErrors,
        ValidationWarnings:     diff.Output.ValidationWarnings,
        ReconciliationProblems: diff.Output.ReconciliationProblems,
        StaleDocCount:          diff.Output.StaleDocCount,
        MojibakeRepairedPages:  diff.Output.MojibakeRepairedPages,
        DetectedTableCount:     diff.Output.DetectedTableCount,
        DocsWithoutHeadings:    diff.Output.DocsWithoutHeadings,
        MissingTitleCount:      diff.Output.MissingTitleCount,
        MissingVersionCount:    diff.Output.MissingVersionCount,
        MissingDepartmentCount: diff.Output.MissingDepartmentCount,
        TraceabilityGapCount:   diff.Output.TraceabilityGapCount,
        Issues:                 diff.Output.Issues,
        RedFlags:               [.. diff.Output.RedFlags, .. extraRedFlags],
        SpotCheckSample:        diff.Output.SpotCheckSample)
    {
        // The run's bill rides the metrics row into the index-run report (plan 1.5) - the
        // report totals and the CuAnalyzePages/CuContextualizationTokens meters now say the
        // same thing from the same source.
        BilledPagesStandard           = diff.Output.BilledPagesStandard,
        BilledContextualizationTokens = diff.Output.BilledContextualizationTokens,
        // The per-model half rides along (2026-08-27) - same source, same row, so the report
        // totals, the CuModelTokens meter and the log line all say the same thing.
        BilledTokensByModel           = diff.Output.BilledTokensByModel,
    };
}

internal record DiffResult(
    string                      Source,
    PdfExtractionOutput         Output,
    List<PdfExtractionDocument> ToProcess,
    List<string>                RemovedSourceIds,
    List<string>                StaleDocumentIds,
    int                         NewCount,
    int                         Updated,
    int                         Skipped);
