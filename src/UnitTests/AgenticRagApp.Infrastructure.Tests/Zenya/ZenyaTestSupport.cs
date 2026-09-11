using System.Net;
using System.Text;

namespace RagApp.UnitTests.Infrastructure.Zenya;

// Scripted HTTP responses for the Zenya client tests: each call to the handler dequeues one
// response and records the request (headers + body) so the wire shape can be asserted. The
// last scripted response is not repeated - running out is an assertion failure, so a test that
// expects exactly two calls fails if the client makes three.
internal sealed class ScriptedHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

    public List<HttpRequestMessage> Requests { get; } = new();
    public List<string?> RequestBodies { get; } = new();

    public ScriptedHandler Enqueue(HttpStatusCode status, string body, string mediaType = "application/json",
        Action<HttpResponseMessage>? configure = null)
    {
        _responses.Enqueue(_ =>
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, mediaType),
            };
            configure?.Invoke(response);
            return response;
        });
        return this;
    }

    public ScriptedHandler EnqueueJson(string body) => Enqueue(HttpStatusCode.OK, body);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        RequestBodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(ct));
        if (_responses.Count == 0)
            throw new InvalidOperationException(
                $"Unexpected request #{Requests.Count}: {request.Method} {request.RequestUri} - no response scripted.");
        return _responses.Dequeue()(request);
    }
}

// Clock the tests can move, so token expiry is tested without waiting ten minutes.
internal sealed class ManualTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 9, 10, 9, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

internal static class ZenyaTestData
{
    public static readonly Uri BaseUrl = new("https://tenant.zenya.work/api/");

    public const string TokenJson = """{ "access_token": "tok-1", "token_type": "Bearer", "expires_in": 600 }""";

    // Verbatim shape from D173 §2a - the anonymous answer that looked like success on 2026-08-28.
    public const string AnonymousUserJson = """
        { "user_id": "1a2b3c4d-0000-4000-8000-000000000002", "login_code": "Anonymous",
          "name": "Anoniem", "user_type": "system_user", "organization_units": [],
          "email_address": "", "created_datetime": "20180731191836",
          "last_modified_datetime": "20250310183649" }
        """;

    public const string ServiceUserJson = """
        { "user_id": "9d1f0c2a-0000-4000-8000-000000000001", "login_code": "RAG_API_User",
          "name": "RAG API", "user_type": "local_user", "organization_units": [] }
        """;

    public static string Page(int offset, int limit, int total, params (string Id, int Version)[] docs)
    {
        var data = string.Join(",", docs.Select(d =>
            $$"""{ "document_id": "{{d.Id}}", "version": {{d.Version}}, "title": "Doc {{d.Id}}", "published_date_time": "20260101120000" }"""));
        return $$"""
            { "data": [{{data}}],
              "extra_data": null,
              "pagination": { "limit": {{limit}}, "offset": {{offset}}, "returned": {{docs.Length}}, "total": {{total}} } }
            """;
    }

    public const string PdfMetadataJson = """
        { "document_id": "doc-1", "version": 3, "revision": 1, "title": "Hygiënecode",
          "type": "file", "document_type": { "id": 3, "name": "Protocol" }, "state": "published", "mime_type": "application/pdf",
          "download_binary_extension": "pdf", "download_as_pdf": true,
          "can_download_binary": true, "can_download_content": false,
          "quick_code": "HYG-001", "active": true, "last_modified_datetime": "20260315101500" }
        """;
}
