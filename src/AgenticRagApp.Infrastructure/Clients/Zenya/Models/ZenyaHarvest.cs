using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgenticRagApp.Infrastructure.Clients.Zenya.Models;

// One raw answer from Zenya, exactly as it came off the wire. Status is recorded for EVERY call,
// success or not: a 403 on /hyperlinks is information ("not permitted"), and it is the only thing
// that separates not-permitted from not-present once the answer is in storage (D243 Part 2).
//
// Body is a JsonElement so the harvest file serialises it as JSON, not as an escaped string. A
// non-JSON body (a proxy answering HTML, an empty 204) lands in Text instead, so nothing is thrown
// away and nothing is parsed twice.
public sealed record ZenyaRawResponse(
    [property: JsonPropertyName("route")]       string Route,
    [property: JsonPropertyName("status")]      int Status,
    [property: JsonPropertyName("contentType")] string? ContentType,
    [property: JsonPropertyName("body")]        JsonElement? Body,
    [property: JsonPropertyName("text")]        string? Text = null)
{
    public bool IsSuccess => Status is >= 200 and < 300;
}

// The per-document sidecar, meta/{document_id}.json (D243 Part 2). Bodies verbatim under an
// envelope that says which route produced each, when, and against which harvest shape.
//
// HarvestVersion is bumped whenever a route is added or a query string changes (D243 Part 3, rule
// 3). A reader then knows which entries to expect, and a re-harvest can target only the sidecars
// written below the current version instead of the whole corpus.
public sealed record ZenyaHarvest(
    [property: JsonPropertyName("document_id")]    string DocumentId,
    [property: JsonPropertyName("version")]        int? Version,
    [property: JsonPropertyName("harvestedAt")]    DateTimeOffset HarvestedAt,
    [property: JsonPropertyName("apiVersion")]     string ApiVersion,
    [property: JsonPropertyName("harvestVersion")] int HarvestVersion,
    [property: JsonPropertyName("responses")]      IReadOnlyList<ZenyaRawResponse> Responses)
{
    // The tenant-level harvest (_tenant/{runId}.json) uses the same envelope with this id and no
    // version, so one reader handles both files.
    public const string TenantDocumentId = "_tenant";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ToJson() => JsonSerializer.Serialize(this, WriteOptions);

    // Route lookup by the exact string the harvester recorded. Null when the route is not in the
    // file at all, which a reader must treat as "not harvested" rather than "empty" (D243 Part 3).
    public ZenyaRawResponse? Route(string route) =>
        Responses.FirstOrDefault(r => string.Equals(r.Route, route, StringComparison.Ordinal));
}
