using AgenticRagApp.Indexing.Csv.Models;

namespace AgenticRagApp.Indexing.Csv.Services;

public interface IDataCleaner
{
    CleanResult Clean(IReadOnlyList<JoinedPageRecord> pages);
}
