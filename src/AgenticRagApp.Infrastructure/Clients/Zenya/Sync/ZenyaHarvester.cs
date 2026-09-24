using AgenticRagApp.Infrastructure.Clients.Zenya.Models;
using Microsoft.Extensions.Logging;

namespace AgenticRagApp.Infrastructure.Clients.Zenya.Sync;

// Asks Zenya for everything it will say about a document and returns it raw (D243 Part 1).
//
// Two decisions live here and nowhere else:
//
//  1. The query strings. Zenya gates optional blocks behind include_* parameters PER ROUTE, and
//     the per-version route carries the seven that hold the person, lock and delegation fields -
//     the ones that were empty on all 1,265 documents because the sync only ever called the
//     unversioned route (D242 §2). Every flag Zenya offers is on. /fields deliberately OMITS
//     shown_to_readers: present-and-true narrows to reader-visible fields, absent means all.
//
//  2. Raw, not modelled. Nothing here deserialises into a typed record, because
//     System.Text.Json drops unknown members silently and a typed harvest could only ever hold
//     the fields someone modelled on the day they wrote it - which is exactly how the question
//     "are we getting everything" became unanswerable from our own data. The typed models stay
//     for what the sync's control flow branches on (ZenyaSyncService); this stores what Zenya said.
//
// Non-2xx answers are recorded, not thrown (IZenyaClient.GetRawAsync): a 403 is a fact about
// this document for this user, and the reader needs it (D243 Part 3).
public sealed class ZenyaHarvester
{
    // Bump on any route added or query string changed. Readers key on it; --reharvest targets
    // sidecars below it. History: 1 = 2026-09-24, D243.
    public const int HarvestVersion = 1;

    // The five listing blocks, as one query fragment so the sync and the harvester agree.
    public const string ListingIncludes =
        "&include_involved_persons=true&include_check_info=true&include_read_roles=true" +
        "&include_writer_invitations=true&include_custom_fields=true";

    private const string VersionIncludes =
        "?include_authors=true&include_authorizers=true&include_document_administrators=true" +
        "&include_writers_group=true&include_invited_writers=true" +
        "&include_check_task_delegated_to_user=true&include_lock_info=true";

    // Route keys as recorded in the sidecar - the reader looks these up by exact string, so they
    // are constants here rather than rebuilt at read time.
    public const string RouteListingRow  = "documents#row";
    public const string RouteDocument    = "documents/{id}?include_print_forced_header=true";
    public const string RouteVersion     = "documents/{id}/v{version}" + VersionIncludes;
    public const string RouteFields      = "documents/{id}/fields?include_meta_field_type=true";
    public const string RouteHyperlinks  = "documents/{id}/hyperlinks";
    public const string RouteMediaItems  = "documents/{id}/mediaitems";
    public const string RouteMediaItems2 = "documents/{id}/media_items";
    public const string RouteContents    = "documents/{id}/v{version}/contents";

    public const string RouteCustomFields = "documents/custom_fields";
    public const string RouteListItems    = "documents/custom_fields/{field_id}/list_items";
    public const string RouteFolder       = "documents/folders/{folder_id}";
    public const string RouteMe           = "users/me";

    private readonly IZenyaClient _zenya;
    private readonly ILogger<ZenyaHarvester> _logger;
    private readonly TimeProvider _time;

    public ZenyaHarvester(IZenyaClient zenya, ILogger<ZenyaHarvester> logger, TimeProvider? time = null)
    {
        _zenya  = zenya;
        _logger = logger;
        _time   = time ?? TimeProvider.System;
    }

    // Everything Zenya will say about one document. listingRow is the raw data[] element from
    // the listing (already fetched, so it costs nothing to keep); contents is only asked for when
    // the document has no binary, which is the 19 authored documents (D185 §5).
    public async Task<ZenyaHarvest> HarvestDocumentAsync(
        string documentId, int version, bool hasBinary, System.Text.Json.JsonElement? listingRow, CancellationToken ct = default)
    {
        var id = Uri.EscapeDataString(documentId);
        var responses = new List<ZenyaRawResponse>(8);

        if (listingRow is { } row)
            responses.Add(new ZenyaRawResponse(RouteListingRow, 200, "application/json", row));

        responses.Add(await Get(RouteDocument,    $"documents/{id}?include_print_forced_header=true", ct));
        responses.Add(await Get(RouteVersion,     $"documents/{id}/v{version}{VersionIncludes}", ct));
        responses.Add(await Get(RouteFields,      $"documents/{id}/fields?include_meta_field_type=true", ct));
        responses.Add(await Get(RouteHyperlinks,  $"documents/{id}/hyperlinks", ct));
        responses.Add(await Get(RouteMediaItems,  $"documents/{id}/mediaitems", ct));
        responses.Add(await Get(RouteMediaItems2, $"documents/{id}/media_items", ct));
        if (!hasBinary)
            responses.Add(await Get(RouteContents, $"documents/{id}/v{version}/contents", ct));

        return new ZenyaHarvest(documentId, version, _time.GetUtcNow(), ZenyaClient.ApiVersion, HarvestVersion, responses);
    }

    // What is true of the tenant rather than of one document: the custom-field catalogue and each
    // list field's allowed values, the folders the corpus actually uses, and the full identity we
    // run as. Once per run, into _tenant/{runId}.json.
    public async Task<ZenyaHarvest> HarvestTenantAsync(IReadOnlyCollection<int> folderIds, CancellationToken ct = default)
    {
        var responses = new List<ZenyaRawResponse>();

        var fields = await Get(RouteCustomFields, "documents/custom_fields", ct);
        responses.Add(fields);

        // list_items only exists for list-typed fields; asking for every field id and recording
        // the 4xx on the others is cheaper than modelling the catalogue to find out which is which.
        foreach (var fieldId in FieldIds(fields))
            responses.Add(await Get(RouteListItems.Replace("{field_id}", fieldId.ToString()),
                                    $"documents/custom_fields/{fieldId}/list_items", ct));

        foreach (var folderId in folderIds.Distinct().Order())
            responses.Add(await Get(RouteFolder.Replace("{folder_id}", folderId.ToString()),
                                    $"documents/folders/{folderId}", ct));

        responses.Add(await Get(RouteMe, "users/me", ct));

        return new ZenyaHarvest(ZenyaHarvest.TenantDocumentId, null, _time.GetUtcNow(), ZenyaClient.ApiVersion, HarvestVersion, responses);
    }

    private async Task<ZenyaRawResponse> Get(string routeKey, string relativePath, CancellationToken ct)
    {
        var raw = await _zenya.GetRawAsync(relativePath, ct);
        if (!raw.IsSuccess)
            _logger.LogInformation("Harvest {Route} -> {Status} (recorded).", relativePath, raw.Status);
        return raw with { Route = routeKey };
    }

    private static IEnumerable<int> FieldIds(ZenyaRawResponse catalogue)
    {
        if (!catalogue.IsSuccess || catalogue.Body is not { ValueKind: System.Text.Json.JsonValueKind.Array } arr)
            yield break;
        foreach (var field in arr.EnumerateArray())
            if (field.TryGetProperty("field_id", out var idEl) && idEl.TryGetInt32(out var id))
                yield return id;
    }
}
