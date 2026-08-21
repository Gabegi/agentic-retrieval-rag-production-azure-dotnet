using AgenticRagApp.Indexing.Csv.Models;

namespace AgenticRagApp.Indexing.Csv.Services;

public interface ICsvJoiner
{
    JoinResult Join(IReadOnlyList<PageRecord> pages, IReadOnlyList<IndexRecord> index);
}
