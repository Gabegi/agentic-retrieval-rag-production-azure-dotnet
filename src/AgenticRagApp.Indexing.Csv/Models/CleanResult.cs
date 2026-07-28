using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Indexing.Csv.Models;

public class CleanResult
{
    private readonly List<CleanedPageRecord> _records  = [];
    private readonly List<CleaningError>     _errors   = [];
    private readonly List<CleaningWarning>   _warnings = [];

    public IReadOnlyList<CleanedPageRecord> Records  => _records;
    public IReadOnlyList<CleaningError>     Errors   => _errors;
    public IReadOnlyList<CleaningWarning>   Warnings => _warnings;

    public int DuplicatePagesSkipped { get; private set; }
    public int MojibakeRepairedPages { get; private set; }

    internal void AddRecord(CleanedPageRecord r)  => _records.Add(r);
    internal void AddError(CleaningError e)        => _errors.Add(e);
    internal void AddWarning(CleaningWarning w)    => _warnings.Add(w);
    internal void CountDuplicateSkipped()          => DuplicatePagesSkipped++;
    internal void CountMojibakeRepaired()          => MojibakeRepairedPages++;
}
