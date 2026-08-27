using AgenticRagApp.Common.Models;
namespace AgenticRagApp.Observability.Reports;

public record ExtractionStageMetrics(
    // Which extractor ran (e.g. "pdf", "csv") - reported here rather
    // than accepted as caller input, since exactly one extractor is registered at a time.
    string Source,
    int DocsToProcess,
    int DocsSkipped,
    int DocsNew,
    int DocsUpdated,
    int DocsDeleted,
    // Document IDs (updated + removed) whose stale chunks still need cleanup. Carried forward
    // to EmbedAndUploadActivity rather than deleted here - see ExtractionService.ExtractAsync.
    IReadOnlyList<string> StaleDocumentIds,
    int ValidationErrors,
    int ValidationWarnings,
    int ReconciliationProblems,
    int? StaleDocCount,          // null = source has no equivalent concept, not "verified zero"
    int MojibakeRepairedPages,
    int DetectedTableCount,
    int DocsWithoutHeadings,
    int MissingTitleCount,
    int? MissingVersionCount,    // null = source has no equivalent concept (PDF since the Zenya removal, 2026-08-26)
    int? MissingDepartmentCount, // null = source has no equivalent concept, not "verified zero"
    // Null = source has no equivalent mechanism - see ExtractionOutputBase.
    int? TraceabilityGapCount,
    IReadOnlyList<PipelineIssue> Issues,
    IReadOnlyList<string>               RedFlags,
    IReadOnlyList<SpotCheckEntry>       SpotCheckSample
)
{
    // What this run billed, in the units the service bills in (observability plan 1.5,
    // 2026-08-26). Init properties rather than positional parameters so the CSV pipeline and
    // every existing constructor call stay untouched - a source with no analyze call simply
    // never sets them. Null = no usage was readable (blank), distinct from 0 (billed nothing).
    // Until these landed, the run totals existed only as a transient log line - the index-run
    // report, the one artifact reviewed after every run, carried no cost at all.
    public long? BilledPagesStandard           { get; init; }
    public long? BilledContextualizationTokens { get; init; }

    // The per-model half of the same bill, keys verbatim as the service bills them (e.g.
    // "gpt-5.4-mini-input"), added 2026-08-27. Until this landed the run total existed only as
    // a transient log line (ExtractionReporter) and a meter - the index-run report, the artifact
    // actually reviewed after every run, carried only the two CU meters above, and those cannot
    // be turned into a TPM figure: BilledContextualizationTokens is a flat 1,000 per page.
    // Empty rather than null when no document reported a token map - unlike the nullable
    // scalars above, "no map" and "no tokens" are the same fact here.
    public IReadOnlyDictionary<string, long> BilledTokensByModel { get; init; } =
        new Dictionary<string, long>();
}
