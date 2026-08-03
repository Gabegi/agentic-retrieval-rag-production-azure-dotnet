using AgenticRagApp.Indexing.Csv.Models;
using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Indexing.Csv.Services;

public interface ICsvExtractor
{
    ExtractionBatch<PageRecord>  ExtractPages(Stream stream);
    ExtractionBatch<IndexRecord> ExtractIndex(Stream stream);
}
