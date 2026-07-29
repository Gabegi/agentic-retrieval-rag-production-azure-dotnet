using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Indexing.Pdf.Models;

// Mirrors CSV's CleanResult. Reuses the existing (source-agnostic) CleaningError/
// CleaningWarning types rather than Pdf-prefixed duplicates.
public class PdfCleanResult
{
    private readonly List<CleanedPdfPageRecord> _records  = [];
    private readonly List<CleaningError>        _errors   = [];
    private readonly List<CleaningWarning>      _warnings = [];

    public IReadOnlyList<CleanedPdfPageRecord> Records  => _records;
    public IReadOnlyList<CleaningError>        Errors   => _errors;
    public IReadOnlyList<CleaningWarning>      Warnings => _warnings;

    public int MojibakeRepairedPages { get; private set; }

    // Per-transform counts, summed across every page cleaned this run — the only way to
    // tell "the cleaner ran and changed nothing because the source was clean" apart from
    // "the cleaner didn't actually do its job" without diffing raw vs. cleaned text by hand.
    public int ControlCharsStripped     { get; private set; }
    public int InvisibleCharsStripped   { get; private set; }
    public int LigaturesExpanded        { get; private set; }
    public int HyphenationJoinsRepaired { get; private set; }

    internal void AddRecord(CleanedPdfPageRecord r) => _records.Add(r);
    internal void AddError(CleaningError e)         => _errors.Add(e);
    internal void AddWarning(CleaningWarning w)     => _warnings.Add(w);
    internal void CountMojibakeRepaired()           => MojibakeRepairedPages++;

    internal void AddCleaningCounts(PdfCleaningCounts counts)
    {
        ControlCharsStripped     += counts.ControlChars;
        InvisibleCharsStripped   += counts.InvisibleChars;
        LigaturesExpanded        += counts.Ligatures;
        HyphenationJoinsRepaired += counts.HyphenJoins;
    }
}

// One page's transform counts, before they're summed into PdfCleanResult's run-level totals.
public readonly record struct PdfCleaningCounts(int ControlChars, int InvisibleChars, int Ligatures, int HyphenJoins);
