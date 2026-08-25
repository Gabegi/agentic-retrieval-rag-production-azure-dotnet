using Azure.AI.ContentUnderstanding;

namespace AgenticRagApp.Infrastructure.Clients.ContentUnderstanding;

// One document in, the finished analysis out. The seam exists so ExtractionService can be tested
// without a real analyze call; there is nothing else to abstract over.
public interface IContentAnalysisClient
{
    Task<AnalysisResult> AnalyzeAsync(byte[] bytes, CancellationToken ct = default);
}
