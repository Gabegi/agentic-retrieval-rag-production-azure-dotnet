#:project ../../AgenticRagApp.Infrastructure/AgenticRagApp.Infrastructure.csproj
#:package Microsoft.Extensions.Hosting
#:property PublishAot=false

using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticRagApp.Infrastructure.Clients.Zenya;
using AgenticRagApp.Infrastructure.Clients.Zenya.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Read-only probe: asks Zenya what each endpoint ACTUALLY returns, and diffs that against what
// our models declare. It exists because a typed client cannot answer "are we getting everything
// we could" - System.Text.Json drops unknown fields silently, so a field Zenya sends and we do
// not model is invisible everywhere else in this codebase (D239 §5, D240).
//
// WHAT IT REPORTS: field NAMES and their shape, never values. Zenya's payloads carry staff names
// and a service user's own identity; a probe artifact that leaks those into a report folder or a
// pipeline log is a privacy problem, not a measurement. `--values` opts in to one redacted sample
// per path (first 40 chars) when a shape genuinely cannot be read from the name alone - use it
// only on a tenant-safe subset, never on the person blocks.
//
// WHAT IT TOUCHES: nothing. Every call is a GET; IZenyaClient is read-only by design and this
// tool does not even use it - it holds the token and issues raw requests so the response body is
// read as JSON text rather than through a model that would hide the answer.
//
// HOW IT AUTHENTICATES: identically to src/Tools/ZenyaSync/ZenyaSync.cs - the same AddZenyaClient wiring,
// the same ZENYA_* environment variables from the zenya-<env> variable group, the same
// DefaultAzureCredential. That resolves to AzureCliCredential inside an AzureCLI@2 task on the
// con-cap-zenyasync-<env> service connection, which is the identity Zenya's registration trusts
// (D175 Path B). On a developer machine `az login` gives a DIFFERENT principal, so the token
// exchange is expected to fail there with "Zenya did not accept the Entra token" - that is the
// tool working correctly, not a bug. Run it in the pipeline.
//
// 2026-09-24, D239 §5. `#:property PublishAot=false` is load-bearing for the same reason it is on
// ZenyaSync.cs - see that file's header before touching the directives.

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddZenyaClient(builder.Configuration);
using var host = builder.Build();

var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ZenyaProbe");
var tokens = host.Services.GetRequiredService<IZenyaTokenProvider>();
var http   = host.Services.GetRequiredService<IHttpClientFactory>()
                 .CreateClient(ZenyaServiceCollectionExtensions.HttpClientName);

var argList     = args.ToList();
int docSample   = IntArg("--docs", 5);
bool withValues = argList.Contains("--values");
string outPath  = StringArg("--out", "zenya-probe.json");
var explicitIds = argList.Where(a => Guid.TryParse(a, out _)).ToList();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var ct = cts.Token;

var report = new Dictionary<string, object?>(StringComparer.Ordinal)
{
    ["probedAt"]   = DateTimeOffset.UtcNow.ToString("O"),
    ["valuesIncluded"] = withValues,
    ["endpoints"]  = new List<object>(),
};
var endpoints = (List<object>)report["endpoints"]!;

try
{
    // ── 1. identity ────────────────────────────────────────────────────────────────────────
    var me = await ProbeAsync("users/me");

    // ── 2. the listing, without and with every include_* flag ──────────────────────────────
    const string listBase = "documents?limit={0}&offset=0&envelope=true&include_total=true";
    var bare = await ProbeAsync(string.Format(listBase, docSample));
    var rich = await ProbeAsync(string.Format(listBase, docSample) +
        "&include_involved_persons=true&include_check_info=true&include_read_roles=true" +
        "&include_writer_invitations=true&include_custom_fields=true");

    // The whole point of the pair: what the flags actually added for THIS service user.
    var added = rich.Paths.Keys.Except(bare.Paths.Keys, StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToList();
    endpoints.Add(new Dictionary<string, object?>
    {
        ["endpoint"] = "COMPARISON: listing with all five include_* flags vs without",
        ["pathsAddedByTheFlags"] = added,
        ["verdict"] = added.Count == 0
            ? "The flags added NOTHING for this service user. Either the blocks are empty for this tenant or the user cannot see them - Zenya returns absent, not denied, so these two stay indistinguishable without asking Zenya."
            : $"The flags added {added.Count} field path(s). Everything listed is available today for one query-string change and no extra call.",
    });

    // ── 3. the per-document DTO, raw, diffed against our model ─────────────────────────────
    var ids = explicitIds.Count > 0 ? explicitIds : IdsFrom(bare.Raw, docSample);
    if (ids.Count == 0) logger.LogWarning("No document ids found in the listing response; per-document probes skipped.");

    var dtoUnion = new SortedSet<string>(StringComparer.Ordinal);
    string? authoredId = null; int authoredVersion = 0;

    foreach (var id in ids)
    {
        var doc = await ProbeAsync($"documents/{Uri.EscapeDataString(id)}");
        foreach (var p in doc.Paths.Keys) dtoUnion.Add(p);

        // The contents route only matters for a document with no binary (the 19 authored ones,
        // D185 §5). Find one while we are here rather than guessing an id.
        if (authoredId is null && doc.Body is not null && TryRoot(doc.Body, out var root)
            && root.TryGetProperty("can_download_binary", out var cdb)
            && cdb.ValueKind == JsonValueKind.False)
        {
            authoredId = id;
            authoredVersion = root.TryGetProperty("version", out var v) && v.TryGetInt32(out var vi) ? vi : 0;
        }
    }

    // Reflection over the model, so this diff cannot drift from the code the way a hand-written
    // list would: every [JsonPropertyName] on the DTO, compared to the top-level wire paths.
    var modelled = typeof(ZenyaDocumentMetadata)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name)
        .Where(n => n is not null).Select(n => n!)
        .ToHashSet(StringComparer.Ordinal);

    var wireTop   = dtoUnion.Where(p => !p.Contains('.') && !p.Contains('[')).ToHashSet(StringComparer.Ordinal);
    var unmodelled = wireTop.Except(modelled, StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToList();
    var neverSeen  = modelled.Except(wireTop, StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToList();

    endpoints.Add(new Dictionary<string, object?>
    {
        ["endpoint"] = $"COMPARISON: GET documents/{{id}} wire fields vs ZenyaDocumentMetadata ({ids.Count} document(s) sampled)",
        ["modelledFieldCount"] = modelled.Count,
        ["wireTopLevelFieldCount"] = wireTop.Count,
        ["onTheWireButNotModelled"] = unmodelled,
        ["modelledButAbsentOnEverySampledDocument"] = neverSeen,
        ["verdict"] = unmodelled.Count == 0
            ? "We model every field the sampled documents carried. Zenya omits nulls, so a wider sample can still surface more."
            : $"{unmodelled.Count} field(s) arrive on the wire and are dropped silently by our DTO.",
    });

    // ── 4. the routes nobody has recorded ──────────────────────────────────────────────────
    if (ids.Count > 0)
    {
        await ProbeAsync($"documents/{Uri.EscapeDataString(ids[0])}/fields");
        await ProbeAsync($"documents/{Uri.EscapeDataString(ids[0])}/hyperlinks");
        await ProbeAsync($"documents/{Uri.EscapeDataString(ids[0])}/mediaitems");
    }

    // ── 5. authored content - the 19 documents that reach no index today ───────────────────
    if (authoredId is not null)
        await ProbeAsync($"documents/{Uri.EscapeDataString(authoredId)}/v{authoredVersion}/contents");
    else
        logger.LogInformation("No document with can_download_binary=false in the sample; /contents not probed. Pass ids explicitly to target one.");

    await File.WriteAllTextAsync(outPath,
        JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), ct);
    logger.LogInformation("Probe report written to {Path}", Path.GetFullPath(outPath));
    return 0;
}
catch (Exception ex)
{
    logger.LogError(ex, "Probe failed.");
    // Still write what was gathered: a probe that dies on call 6 has already answered calls 1-5.
    try
    {
        await File.WriteAllTextAsync(outPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);
        logger.LogInformation("Partial report written to {Path}", Path.GetFullPath(outPath));
    }
    catch { /* the original exception is what matters */ }
    return 1;
}

// ── helpers ────────────────────────────────────────────────────────────────────────────────

async Task<ProbeResult> ProbeAsync(string relativePath)
{
    var token = await tokens.GetTokenAsync(ct);
    using var request = new HttpRequestMessage(HttpMethod.Get, relativePath);
    request.Headers.TryAddWithoutValidation(ZenyaClient.ApiVersionHeader, ZenyaClient.ApiVersion);
    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    request.Headers.Authorization = new AuthenticationHeaderValue(token.Scheme, token.Token);

    using var response = await http.SendAsync(request, ct);
    var body = await response.Content.ReadAsStringAsync(ct);

    var entry = new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["endpoint"]    = relativePath,
        ["status"]      = (int)response.StatusCode,
        ["contentType"] = response.Content.Headers.ContentType?.ToString(),
        ["bytes"]       = body.Length,
    };

    var paths = new SortedDictionary<string, PathFact>(StringComparer.Ordinal);
    JsonDocument? parsed = null;
    if (body.Length > 0 && (response.Content.Headers.ContentType?.MediaType?.Contains("json") ?? false))
    {
        try
        {
            parsed = JsonDocument.Parse(body);
            Walk(parsed.RootElement, "", paths);
            entry["fields"] = paths.ToDictionary(
                kv => kv.Key,
                kv => (object)new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["kind"]   = string.Join("|", kv.Value.Kinds.OrderBy(k => k, StringComparer.Ordinal)),
                    ["seen"]   = kv.Value.Seen,
                    ["nulls"]  = kv.Value.Nulls,
                    ["sample"] = withValues ? kv.Value.Sample : null,
                });
        }
        catch (JsonException ex) { entry["parseError"] = ex.Message; }
    }
    else if (body.Length > 0)
    {
        // A proxy answering HTML is a real failure mode here and must not read as "no fields".
        entry["nonJsonBodyHead"] = body[..Math.Min(200, body.Length)];
    }

    endpoints.Add(entry);
    logger.LogInformation("GET {Path} -> {Status}, {Fields} distinct field path(s)",
        relativePath, (int)response.StatusCode, paths.Count);
    return new ProbeResult(paths, parsed?.RootElement.Clone(), body);
}

// Collapses arrays to `[]` so ten rows of the same shape report one path, not ten.
static void Walk(JsonElement el, string prefix, SortedDictionary<string, PathFact> into)
{
    switch (el.ValueKind)
    {
        case JsonValueKind.Object:
            foreach (var p in el.EnumerateObject())
            {
                var path = prefix.Length == 0 ? p.Name : $"{prefix}.{p.Name}";
                Record(path, p.Value, into);
                Walk(p.Value, path, into);
            }
            break;
        case JsonValueKind.Array:
            foreach (var item in el.EnumerateArray()) Walk(item, prefix + "[]", into);
            break;
    }
}

static void Record(string path, JsonElement value, SortedDictionary<string, PathFact> into)
{
    if (!into.TryGetValue(path, out var fact))
        into[path] = fact = new PathFact();
    fact.Seen++;
    fact.Kinds.Add(value.ValueKind.ToString());
    if (value.ValueKind is JsonValueKind.Null) fact.Nulls++;
    else if (fact.Sample is null && value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
    {
        var raw = value.ToString();
        fact.Sample = raw.Length > 40 ? raw[..40] + "…" : raw;
    }
}

static bool TryRoot(JsonElement? el, out JsonElement root)
{
    root = el ?? default;
    return el is { ValueKind: JsonValueKind.Object };
}

static List<string> IdsFrom(string body, int take)
{
    var ids = new List<string>();
    try
    {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind == JsonValueKind.Object &&
            doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var row in data.EnumerateArray())
                if (ids.Count < take && row.TryGetProperty("document_id", out var id) && id.GetString() is { } s)
                    ids.Add(s);
    }
    catch (JsonException) { /* the caller logs the empty result */ }
    return ids;
}

int IntArg(string name, int fallback)
{
    var i = argList.IndexOf(name);
    return i >= 0 && i + 1 < argList.Count && int.TryParse(argList[i + 1], out var v) ? v : fallback;
}

string StringArg(string name, string fallback)
{
    var i = argList.IndexOf(name);
    return i >= 0 && i + 1 < argList.Count ? argList[i + 1] : fallback;
}

sealed class PathFact
{
    public int Seen;
    public int Nulls;
    public HashSet<string> Kinds { get; } = new(StringComparer.Ordinal);
    public string? Sample { get; set; }
}

sealed record ProbeResult(SortedDictionary<string, PathFact> Paths, JsonElement? Body, string Raw);
