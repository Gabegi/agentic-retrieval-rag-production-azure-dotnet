using System.Text.Json.Serialization;

namespace AgenticRagApp.Querying.Models;

// The wire shape of a query answer: what POST /api/query returns, on whichever host serves it.
// The Functions host (QueryingFunction) and the App Service host (AgenticRagApp.Api's
// QueryEndpoint) both map through From, so a client moves between the two by changing the base
// URL and nothing else. Typed, with every JSON name pinned by attribute, because a frontend
// outside this repo (OutSystems, planned) will consume it from the OpenAPI document; the
// anonymous object each host used to build gave two contracts that could drift, and no
// document to import. The names are the ones the Functions host has always sent - nothing on
// the wire changed when this record was introduced (2026-09-16).
public sealed record QueryResponse(
    [property: JsonPropertyName("answer")]    string                     Answer,
    [property: JsonPropertyName("category")]  string?                    Category,
    [property: JsonPropertyName("sources")]   IReadOnlyList<QuerySource> Sources,
    [property: JsonPropertyName("telemetry")] QueryTelemetry             Telemetry)
{
    public static QueryResponse From(RagQueryResult result) => new(
        Answer:    result.Answer,
        Category:  result.Category,
        Sources:   result.Citations.Select(QuerySource.From).ToList(),
        Telemetry: new QueryTelemetry(result.LatencyMs, result.InputTokens, result.OutputTokens));
}

public sealed record QuerySource(
    [property: JsonPropertyName("document_id")]   string          DocumentId,
    [property: JsonPropertyName("title")]         string?         Title,
    [property: JsonPropertyName("quick_code")]    string?         QuickCode,
    [property: JsonPropertyName("relative_path")] string?         RelativePath,
    [property: JsonPropertyName("page")]          int?            Page,
    [property: JsonPropertyName("page_count")]    int?            PageCount,
    [property: JsonPropertyName("created_at")]    DateTimeOffset? CreatedAt,
    [property: JsonPropertyName("mod_date")]      DateTimeOffset? ModDate,
    [property: JsonPropertyName("label")]         string?         Label,
    [property: JsonPropertyName("url")]           string?         Url)
{
    public static QuerySource From(Citation c) => new(
        DocumentId:   c.DocumentId,
        Title:        c.Title,
        QuickCode:    c.QuickCode,
        RelativePath: c.RelativePath,
        Page:         c.Page,
        PageCount:    c.PageCount,
        CreatedAt:    c.CreatedAt,
        ModDate:      c.ModDate,
        // Criterion 7: "[Title] - p.(page number)" — the acceptance criterion's own
        // example (`[Vilans protocollen voor neustampon] - p.2`) has no parentheses
        // around the page number despite its prose header reading "p.(page number)";
        // built to match the example. Both the pre-formatted label and the raw
        // title/page fields are sent, since no frontend exists in this repo to confirm
        // which one is expected to do the formatting - see
        // docs/2608/260806/remaining-acceptance-criteria-plan.md, item 4.
        Label:        c.Title is not null && c.Page is not null
            ? $"[{c.Title}] - p.{c.Page}"
            : c.Title,
        // url was zenya_url, removed with the Zenya metadata mechanism
        // (2026-08-26) - kept as a key so API consumers keep deserializing.
        Url:          null);
}

public sealed record QueryTelemetry(
    [property: JsonPropertyName("latency_ms")]    long LatencyMs,
    [property: JsonPropertyName("input_tokens")]  long InputTokens,
    [property: JsonPropertyName("output_tokens")] long OutputTokens);
