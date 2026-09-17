namespace AgenticRagApp.Indexing.CU.Services;

// Every chunk in a run failed its vector health check, so none was sent to Azure AI Search
// (UploadService.GuardAgainstTotalWithhold, D199 A1).
//
// Typed rather than a bare InvalidOperationException, and carrying the numbers as PROPERTIES
// rather than only inside the message: whatever reports this failure - today the host log, and
// a failed-run report row if D199 §8b item 3 lands - should read structured data, not a regex
// over prose that will be reworded. The message is built FROM the properties so the two cannot
// disagree.
//
// A total withhold is a configuration fault, not a data-quality event: at 100% the cause is
// systemic, and the run must fail rather than report success with DocsUploaded = 0 while every
// stale row is retained.
public sealed class TotalWithholdException : InvalidOperationException
{
    public TotalWithholdException(
        int totalChunks,
        int distinctDocuments,
        int expectedDimensions,
        IReadOnlyDictionary<string, int> verdictCounts,
        bool isDimensionDrift)
        : base(BuildMessage(totalChunks, expectedDimensions, verdictCounts, isDimensionDrift))
    {
        TotalChunks        = totalChunks;
        DistinctDocuments  = distinctDocuments;
        ExpectedDimensions = expectedDimensions;
        VerdictCounts      = verdictCounts;
        IsDimensionDrift   = isDimensionDrift;
    }

    // Chunks the run had; equal to the number withheld, which is what makes this a total withhold.
    public int TotalChunks { get; }

    // Distinct documents those chunks came from. Carried so the failed run's report can fill
    // DocumentsWithheld as an ordinary run would, rather than leaving the column blank on the one
    // run where it is most diagnostic.
    public int DistinctDocuments { get; }

    // The width every vector was judged against: the LIVE index field width, read at preflight
    // (D201). Not OPENAI_EMBEDDING_DIMENSIONS - that is what the index was asked for when it was
    // created, which is not necessarily what it is.
    public int ExpectedDimensions { get; }

    // Verdict name -> count, e.g. { "WrongWidth": 3711 }. "NoVector" for chunks that had no
    // vector at all, which Classify cannot produce a verdict for.
    public IReadOnlyDictionary<string, int> VerdictCounts { get; }

    // True when EVERY withheld chunk was WrongWidth - the signature of the configured dimensions
    // drifting from the index's content_vector field, and the one case that sends the reader to
    // configuration rather than to the embedding deployment. A reporter can branch on this
    // without re-deriving it from VerdictCounts.
    public bool IsDimensionDrift { get; }

    private static string BuildMessage(
        int totalChunks, int expectedDimensions, IReadOnlyDictionary<string, int> verdictCounts, bool isDimensionDrift)
    {
        var diagnosis = isDimensionDrift
            ? $"every vector is the wrong width for the index's {expectedDimensions}-wide vector field — the embedding deployment and the index disagree, so either the deployment changed or the index was rebuilt at a width it does not produce. OPENAI_EMBEDDING_DIMENSIONS is not implicated: vectors are judged against the live index, not against configuration (D201)"
            : "the embedding side produced no usable vector for any chunk — check the embedding deployment, not the index schema";

        var breakdown = string.Join(", ", verdictCounts
            .OrderByDescending(p => p.Value)
            .Select(p => $"{p.Key}={p.Value}"));

        return $"Withholding all {totalChunks} chunk(s) from the index: {diagnosis}. Verdicts: {breakdown}.";
    }
}
