using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Indexing.Pdf.Models;

// Mirrors CSV's CleanResult. Reuses the source-agnostic PipelineIssue type rather than
// Pdf-prefixed duplicates.
//
// Issues are accumulated in one list and split by severity on the way out, rather than
// kept in two separately-typed lists. The type system no longer proves "Errors contains
// only errors" - PipelineIssue.Severity does, and it is checked in exactly one place here.
public class PdfCleanResult
{
    private readonly List<CleanedPdfPageRecord> _records = [];
    private readonly List<PipelineIssue>        _issues  = [];

    public IReadOnlyList<CleanedPdfPageRecord> Records  => _records;
    public IReadOnlyList<PipelineIssue>        Issues   => _issues;
    public IReadOnlyList<PipelineIssue>        Errors   => [.. _issues.Where(i => i.IsError)];
    public IReadOnlyList<PipelineIssue>        Warnings => [.. _issues.Where(i => i.IsWarning)];

    public int MojibakeRepairedPages { get; private set; }

    // Per-transform counts, summed across every page cleaned this run — the only way to
    // tell "the cleaner ran and changed nothing because the source was clean" apart from
    // "the cleaner didn't actually do its job" without diffing raw vs. cleaned text by hand.
    public int ControlCharsStripped     { get; private set; }
    public int InvisibleCharsStripped   { get; private set; }
    public int LigaturesExpanded        { get; private set; }
    public int HyphenationJoinsRepaired { get; private set; }

    // A <table> whose shape ConvertTable couldn't parse (no rows, empty grid, zero
    // columns) - previously the whole block was deleted outright with no signal at all;
    // now it falls back to tag-stripped plain text (see ConvertTable), and this counts
    // how often that fallback fired, so a discrepancy against DetectedTableCount is
    // visible instead of silent (finding #16).
    public int TableConversionFallbacks { get; private set; }

    internal void AddRecord(CleanedPdfPageRecord r) => _records.Add(r);
    internal void AddIssue(PipelineIssue issue)     => _issues.Add(issue);
    internal void CountMojibakeRepaired()           => MojibakeRepairedPages++;
    internal void CountTableConversionFallback()    => TableConversionFallbacks++;

    internal void AddCleaningCounts(PdfCleaningCounts counts)
    {
        ControlCharsStripped     += counts.ControlChars;
        InvisibleCharsStripped   += counts.InvisibleChars;
        LigaturesExpanded        += counts.Ligatures;
        HyphenationJoinsRepaired += counts.HyphenJoins;
    }

    // Folds another file's PdfCleanResult (produced by cleaning that one file's pages in
    // isolation - see PdfExtractionPipeline's per-file cleaning inside the extraction loop,
    // finding #14) into this run-level aggregate. Single-threaded by design: callers collect
    // the per-file results from the parallel extraction loop into a thread-safe collection
    // first, then merge them sequentially here - CleanPdf itself only ever mutates the one
    // PdfCleanResult it just created, so no locking is needed inside a single file's clean.
    internal void MergeFrom(PdfCleanResult other)
    {
        _records.AddRange(other._records);
        _issues.AddRange(other._issues);
        MojibakeRepairedPages     += other.MojibakeRepairedPages;
        ControlCharsStripped      += other.ControlCharsStripped;
        InvisibleCharsStripped    += other.InvisibleCharsStripped;
        LigaturesExpanded         += other.LigaturesExpanded;
        HyphenationJoinsRepaired  += other.HyphenationJoinsRepaired;
        TableConversionFallbacks  += other.TableConversionFallbacks;
    }
}

// One page's transform counts, before they're summed into PdfCleanResult's run-level totals.
public readonly record struct PdfCleaningCounts(int ControlChars, int InvisibleChars, int Ligatures, int HyphenJoins);
