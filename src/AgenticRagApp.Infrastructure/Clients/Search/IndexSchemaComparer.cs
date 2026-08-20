using Azure.Search.Documents.Indexes.Models;

namespace AgenticRagApp.Infrastructure.Clients.Search;

// Compares the index schema the code declares (IIndexService.BuildDefinition) against the
// one actually live on the service, and reports the differences in words.
//
// This exists because IndexService is get-or-create by design: a schema change in a deployed
// build never reaches an existing index, only RecreateIndexAsync does that. Nothing else in
// the system notices the gap - indexing keeps succeeding, queries keep returning documents,
// and the only symptom is answers scored against a shape the code stopped declaring. The
// 2026-07-30 incident ('id' not sortable, docs/260730) was exactly this, and was diagnosed
// from failing indexing runs rather than from anything that compared the two definitions.
//
// Deliberately field-level only. Vector-search profiles, semantic configurations, analyzers
// declared at index level, scoring profiles and suggesters are NOT compared: the drift that
// has actually bitten this codebase has been fields and their flags, and a comparer that
// reports cosmetic differences in the parts nobody edits would train its readers to ignore
// it. Widen it when a real incident says otherwise, not before.
public static class IndexSchemaComparer
{
    // Empty means "no drift the caller needs to act on". Each entry is one human-readable
    // difference, ordered field-by-field so two runs against the same pair of schemas produce
    // the same list in the same order.
    public static IReadOnlyList<string> Compare(SearchIndex expected, SearchIndex live)
    {
        var drift = new List<string>();

        var expectedFields = expected.Fields.ToDictionary(f => f.Name, StringComparer.Ordinal);
        var liveFields     = live.Fields.ToDictionary(f => f.Name, StringComparer.Ordinal);

        foreach (var name in expectedFields.Keys.OrderBy(n => n, StringComparer.Ordinal))
        {
            if (!liveFields.TryGetValue(name, out var liveField))
            {
                drift.Add($"field '{name}' is declared in code but missing from the live index");
                continue;
            }

            CompareField(expectedFields[name], liveField, drift);
        }

        // Reported, not ignored: an extra field means the live index was built by a different
        // build than this one - usually an older deployment's schema that has since dropped
        // the field, occasionally a hand-edit in the portal. Either way the two disagree, and
        // "the index has things the code doesn't know about" is the same class of surprise as
        // the reverse.
        foreach (var name in liveFields.Keys.OrderBy(n => n, StringComparer.Ordinal))
            if (!expectedFields.ContainsKey(name))
                drift.Add($"field '{name}' exists on the live index but is not declared in code");

        return drift;
    }

    private static void CompareField(SearchField expected, SearchField live, List<string> drift)
    {
        void Check(string property, object? expectedValue, object? liveValue)
        {
            if (!Equals(expectedValue, liveValue))
                drift.Add($"field '{expected.Name}': {property} is {Describe(expectedValue)} in code, {Describe(liveValue)} on the live index");
        }

        Check("type",         expected.Type.ToString(), live.Type.ToString());
        // Null and false mean the same thing to the service, and which one a field carries
        // depends on whether it was built here (SimpleField/SearchableField set them
        // explicitly) or read back over the wire - so they are normalized before comparing,
        // or every single field would report drift on flags nobody changed.
        Check("IsKey",        expected.IsKey        ?? false, live.IsKey        ?? false);
        Check("IsSearchable", expected.IsSearchable ?? false, live.IsSearchable ?? false);
        Check("IsFilterable", expected.IsFilterable ?? false, live.IsFilterable ?? false);
        Check("IsSortable",   expected.IsSortable   ?? false, live.IsSortable   ?? false);
        Check("IsFacetable",  expected.IsFacetable  ?? false, live.IsFacetable  ?? false);
        Check("IsHidden",     expected.IsHidden     ?? false, live.IsHidden     ?? false);

        // A dimension change is unrecoverable in place and silently catastrophic: every
        // vector already in the index was produced at the old size, so the field has to be
        // caught here rather than at query time.
        Check("vector dimensions",   expected.VectorSearchDimensions,  live.VectorSearchDimensions);
        Check("vector profile",      expected.VectorSearchProfileName, live.VectorSearchProfileName);
        Check("analyzer",            expected.AnalyzerName?.ToString(), live.AnalyzerName?.ToString());
    }

    private static string Describe(object? value) => value switch
    {
        null      => "unset",
        bool flag => flag ? "true" : "false",
        _         => value.ToString() ?? "unset",
    };
}
