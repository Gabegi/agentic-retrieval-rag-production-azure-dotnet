using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using AgenticRagApp.Infrastructure.Clients.Zenya.Models;
using Microsoft.Extensions.Logging;

namespace AgenticRagApp.Infrastructure.Clients.Zenya;

// Hand-rolled against the v5 spec (D155, D173 §1) - Zenya ships no .NET SDK. Kept to the
// read-only DOC surface the sync needs; see IZenyaClient for the contract of each call.
public sealed class ZenyaClient : IZenyaClient
{
    // Zenya defaults to v4 and majors are not backwards compatible; omitting this silently talks
    // to a different API than the spec we read (D155 §1). On every request, token calls included.
    public const string ApiVersionHeader = "x-api-version";
    public const string ApiVersion = "5";

    // Listing page size. 1000 is the documented maximum and default (D173 §1); asking for the
    // max keeps the listing to ceil(N/1000) calls, and the per-document GETs dominate anyway.
    public const int PageSize = 1000;

    private readonly HttpClient _http;
    private readonly IZenyaTokenProvider _tokens;
    private readonly ILogger<ZenyaClient> _logger;

    public ZenyaClient(HttpClient http, IZenyaTokenProvider tokens, ILogger<ZenyaClient> logger)
    {
        _http   = http;
        _tokens = tokens;
        _logger = logger;
    }

    public async Task<ZenyaUser> GetCurrentUserAsync(CancellationToken ct = default) =>
        await GetJsonAsync<ZenyaUser>("users/me", ct);

    public async Task<ZenyaUser> EnsureAuthenticatedAsync(CancellationToken ct = default)
    {
        var me = await GetCurrentUserAsync(ct);
        if (me.IsAnonymous)
        {
            _logger.LogError("Zenya /users/me answered as Anonymous (user_id {UserId}); token not accepted", me.UserId);
            throw new ZenyaAnonymousException();
        }
        _logger.LogInformation("Zenya authenticated as '{Login}' ({Name}, {UserType})", me.LoginCode, me.Name, me.UserType);
        return me;
    }

    public async IAsyncEnumerable<ZenyaDocumentListItem> ListDocumentsAsync(
        IReadOnlyCollection<string>? states = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var offset = 0;
        while (true)
        {
            var query = $"documents?limit={PageSize}&offset={offset}&envelope=true&include_total=true";
            if (states is { Count: > 0 })
                query += string.Concat(states.Select(s => $"&states={Uri.EscapeDataString(s)}"));

            var page = await GetJsonAsync<ZenyaDocumentPage>(query, ct);
            foreach (var item in page.Data)
                yield return item;

            // Stop on the envelope's own accounting, not on an empty page: a short page is the
            // last page, and `total` (when present) bounds the walk against a listing that grows
            // mid-crawl. `returned` == 0 also ends it, so a tenant that omits pagination cannot
            // loop forever.
            var returned = page.Pagination?.Returned ?? page.Data.Count;
            offset += returned;
            var total = page.Pagination?.Total;
            if (returned == 0 || returned < PageSize || (total is { } t && offset >= t))
                yield break;
        }
    }

    public async Task<ZenyaDocumentMetadata> GetDocumentAsync(string documentId, CancellationToken ct = default) =>
        await GetJsonAsync<ZenyaDocumentMetadata>($"documents/{Uri.EscapeDataString(documentId)}", ct);

    public async Task<ZenyaDownload> DownloadAsync(string documentId, int version, CancellationToken ct = default)
    {
        var response = await SendAsync(
            $"documents/{Uri.EscapeDataString(documentId)}/v{version}/download",
            HttpCompletionOption.ResponseHeadersRead, ct);
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(ct);
            return new ZenyaDownload(
                response, stream,
                response.Content.Headers.ContentType?.MediaType,
                response.Content.Headers.ContentLength);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public async Task<ZenyaDocumentContent> GetContentsAsync(string documentId, int version, CancellationToken ct = default) =>
        await GetJsonAsync<ZenyaDocumentContent>(
            $"documents/{Uri.EscapeDataString(documentId)}/v{version}/contents", ct);

    // --- plumbing ---------------------------------------------------------------------------

    private async Task<T> GetJsonAsync<T>(string relativePath, CancellationToken ct)
    {
        using var response = await SendAsync(relativePath, HttpCompletionOption.ResponseContentRead, ct);
        return await response.Content.ReadFromJsonAsync<T>(ct)
               ?? throw new ZenyaApiException(response.StatusCode, null, null,
                   $"Zenya returned {(int)response.StatusCode} with an empty body for GET {relativePath}.");
    }

    // Sends an authenticated GET and returns a success response, or throws ZenyaApiException
    // with the problem body's title / error_code. The 429 loop lives in ZenyaRetryHandler
    // underneath; a 429 reaching here has exhausted its retries.
    private async Task<HttpResponseMessage> SendAsync(string relativePath, HttpCompletionOption completion, CancellationToken ct)
    {
        var token = await _tokens.GetTokenAsync(ct);

        using var request = new HttpRequestMessage(HttpMethod.Get, relativePath);
        AddCommonHeaders(request);
        request.Headers.Authorization = new AuthenticationHeaderValue(token.Scheme, token.Token);

        var response = await _http.SendAsync(request, completion, ct);
        if (response.IsSuccessStatusCode) return response;

        using (response)
        {
            var problem = await TryReadProblemAsync(response, ct);
            throw new ZenyaApiException(
                response.StatusCode, problem?.Title, problem?.ErrorCode,
                $"Zenya GET {relativePath} failed with HTTP {(int)response.StatusCode}" +
                (problem?.Title is { } title ? $": '{title}'" : string.Empty) +
                (problem?.ErrorCode is { } code ? $" (error_code {code}, error_date {problem.ErrorDate})" : string.Empty) +
                ".");
        }
    }

    internal static void AddCommonHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation(ApiVersionHeader, ApiVersion);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    // Best effort: 401 bodies are documented as detail-free, and a proxy may answer with HTML.
    internal static async Task<ZenyaProblem?> TryReadProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            if (response.Content.Headers.ContentLength == 0) return null;
            return await response.Content.ReadFromJsonAsync<ZenyaProblem>(ct);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or NotSupportedException)
        {
            return null;
        }
    }
}
