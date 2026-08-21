using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Indexing.Csv.Models;

public class CleanResult
{
    private readonly List<CleanedPageRecord> _records = [];
    private readonly List<PipelineIssue>     _issues  = [];

    public IReadOnlyList<CleanedPageRecord> Records  => _records;
    public IReadOnlyList<PipelineIssue>     Issues   => _issues;
    public IReadOnlyList<PipelineIssue>     Errors   => [.. _issues.Where(i => i.IsError)];
    public IReadOnlyList<PipelineIssue>     Warnings => [.. _issues.Where(i => i.IsWarning)];

    public int DuplicatePagesSkipped { get; private set; }
    public int MojibakeRepairedPages { get; private set; }

    internal void AddRecord(CleanedPageRecord r)  => _records.Add(r);
    internal void AddIssue(PipelineIssue issue)    => _issues.Add(issue);
    internal void CountDuplicateSkipped()          => DuplicatePagesSkipped++;
    internal void CountMojibakeRepaired()          => MojibakeRepairedPages++;
}
