using Azure;
using Azure.AI.ContentUnderstanding;

namespace AgenticRagApp.Infrastructure.Clients.ContentUnderstanding;

// Submit a PDF to Content Understanding and wait for the result. That is the whole client.
//
// Shaped after the SDK's own Sample01_AnalyzeBinary:
// https://github.com/Azure/azure-sdk-for-net/blob/Azure.AI.ContentUnderstanding_1.1.0/sdk/contentunderstanding/Azure.AI.ContentUnderstanding/samples/Sample01_AnalyzeBinary.md
public sealed class ContentAnalysisClient : IContentAnalysisClient
{
    // The prebuilt analyzer, not a custom one. Two prerequisites on the account, neither of which
    // this app sets up:
    //   1. gpt-4.1-mini and text-embedding-3-large deployments (infra/ai_deployments.tf).
    //   2. The account-wide default model->deployment mapping that points at them.
    // AnalyzeBinaryAsync has no per-request modelDeployments parameter - only the Analyze(inputs)
    // overload does - so #2 is a one-off setup step done outside this app. Miss it and the first
    // call fails with "Model deployment not found".
    private const string AnalyzerId = "prebuilt-documentSearch";

    private readonly ContentUnderstandingClient _client;

    public ContentAnalysisClient(ContentUnderstandingClient client) => _client = client;

    public async Task<AnalysisResult> AnalyzeAsync(byte[] bytes, CancellationToken ct = default)
    {
        BinaryData binaryData = BinaryData.FromBytes(bytes);

        Operation<AnalysisResult> operation = await _client.AnalyzeBinaryAsync(
            WaitUntil.Completed,
            AnalyzerId,
            binaryData,
            cancellationToken: ct);

        return operation.Value;
    }
}
