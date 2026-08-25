using Azure;
using Azure.AI.ContentUnderstanding;

namespace AgenticRagApp.Infrastructure.Clients.ContentUnderstanding;

public class ContentAnalysisClient : IContentAnalysisClient
{
    private readonly ContentUnderstandingClient _client;

    public ContentAnalysisClient(ContentUnderstandingClient client) => _client = client;

    public async Task<Operation<AnalysisResult>> SubmitAnalyzeAsync(
        byte[] bytes, string analyzerId, ContentRange? range = null, CancellationToken ct = default) =>
        await _client.AnalyzeBinaryAsync(
            WaitUntil.Started,
            analyzerId,
            BinaryData.FromBytes(bytes),
            contentRange: range,
            contentType:  "application/pdf",
            // Never left on the SDK default, which is ProcessingLocation.Global - "data may be
            // processed in any Azure data center globally". Geography keeps it in the same
            // geography as the resource (West Europe). This is a Dutch care organisation's
            // document corpus; the default is not an acceptable answer for it.
            // See docs/2608/260819/content-understanding-when-and-how.md:381-384.
            processingLocation: ProcessingLocation.Geography,
            cancellationToken:  ct);
}
