using AgenticRagApp.Infrastructure.Clients.Zenya.Models;

namespace AgenticRagApp.Infrastructure.Clients.Zenya;

// Typed access to the Zenya DOC endpoints the ingestion needs (D173 §1, D175 A4). Every call
// carries x-api-version: 5 and the token from IZenyaTokenProvider; paging and 429 backoff are
// handled inside. Nothing here writes to Zenya.
public interface IZenyaClient
{
    // GET /users/me - who the token authenticates as.
    Task<ZenyaUser> GetCurrentUserAsync(CancellationToken ct = default);

    // GetCurrentUserAsync, then throws ZenyaAnonymousException if Zenya answered as the
    // Anonymous account. Call this before trusting any document count: Zenya answers a bad
    // token with 200 + Anonymous, not 401 (D173 §2a).
    Task<ZenyaUser> EnsureAuthenticatedAsync(CancellationToken ct = default);

    // GET /documents, envelope paging, all pages. `states` null = Zenya's default (published;
    // active, not archived). Which states the corpus needs is an open product question (D175).
    IAsyncEnumerable<ZenyaDocumentListItem> ListDocumentsAsync(
        IReadOnlyCollection<string>? states = null,
        CancellationToken ct = default);

    // GET /documents/{id} - the legacy DTO with the routing flags (can_download_binary /
    // can_download_content), mime type, quick_code and last-modified. One call per document.
    Task<ZenyaDocumentMetadata> GetDocumentAsync(string documentId, CancellationToken ct = default);

    // GET /documents/{id}/v{version}/download - the binary (PDF/Word/...), streamed. Dispose the
    // result to release the connection. 400 = type not downloadable, 403 = no read rights,
    // 404 = no published version (D173 §1) - all surface as ZenyaApiException.
    Task<ZenyaDownload> DownloadAsync(string documentId, int version, CancellationToken ct = default);

    // GET /documents/{id}/v{version}/contents - the typed authored-content route. The `content`
    // string's shape is unknown until A8; returned opaque.
    Task<ZenyaDocumentContent> GetContentsAsync(string documentId, int version, CancellationToken ct = default);
}

// A streamed binary download. Owns the underlying response; dispose when done with the stream.
public sealed class ZenyaDownload : IAsyncDisposable, IDisposable
{
    private readonly HttpResponseMessage _response;

    public ZenyaDownload(HttpResponseMessage response, Stream content, string? contentType, long? contentLength)
    {
        _response     = response;
        Content       = content;
        ContentType   = contentType;
        ContentLength = contentLength;
    }

    public Stream Content { get; }
    public string? ContentType { get; }
    public long? ContentLength { get; }

    public void Dispose()
    {
        Content.Dispose();
        _response.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await Content.DisposeAsync();
        _response.Dispose();
    }
}
