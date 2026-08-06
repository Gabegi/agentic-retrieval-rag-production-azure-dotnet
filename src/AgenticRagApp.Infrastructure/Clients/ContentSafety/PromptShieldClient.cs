using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Azure.Core;

namespace AgenticRagApp.Infrastructure.Clients.ContentSafety;

// Wraps the Prompt Shields REST API (POST {endpoint}/contentsafety/text:shieldPrompt),
// which the Azure.AI.ContentSafety .NET SDK does not expose - only text/image harm
// analysis and blocklist management are wrapped there. Verified against the SDK source
// (no PromptShield-related types in 1.0.0) and the published REST spec
// (learn.microsoft.com/rest/api/contentsafety/text-operations/shield-prompt,
// api-version 2024-09-01, GA).
public sealed class PromptShieldClient : IPromptShieldClient
{
    private const string ApiVersion = "2024-09-01";
    private static readonly string[] Scopes = { "https://cognitiveservices.azure.com/.default" };

    private readonly HttpClient _http;
    private readonly TokenCredential _credential;

    public PromptShieldClient(HttpClient http, TokenCredential credential)
    {
        _http = http;
        _credential = credential;
    }

    public async Task<bool> DetectAttackAsync(
        string userPrompt,
        IReadOnlyList<string>? documents,
        CancellationToken ct = default)
    {
        var token = await _credential.GetTokenAsync(new TokenRequestContext(Scopes), ct);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"contentsafety/text:shieldPrompt?api-version={ApiVersion}")
        {
            Content = JsonContent.Create(new ShieldPromptRequest(userPrompt, documents ?? Array.Empty<string>())),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ShieldPromptResponse>(ct)
                     ?? throw new InvalidOperationException("Prompt Shields returned an empty response body.");

        return result.UserPromptAnalysis.AttackDetected
               || result.DocumentsAnalysis.Any(d => d.AttackDetected);
    }

    private sealed record ShieldPromptRequest(
        [property: JsonPropertyName("userPrompt")] string UserPrompt,
        [property: JsonPropertyName("documents")] IReadOnlyList<string> Documents);

    private sealed record ShieldPromptResponse(
        [property: JsonPropertyName("userPromptAnalysis")] AttackAnalysis UserPromptAnalysis,
        [property: JsonPropertyName("documentsAnalysis")] IReadOnlyList<AttackAnalysis> DocumentsAnalysis);

    private sealed record AttackAnalysis(
        [property: JsonPropertyName("attackDetected")] bool AttackDetected);
}
