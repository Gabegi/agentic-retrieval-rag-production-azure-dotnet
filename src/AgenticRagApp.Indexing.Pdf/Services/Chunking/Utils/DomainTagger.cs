using System.Text.RegularExpressions;

namespace AgenticRagApp.Indexing.Pdf.Utils;

// Pre-chunking action item C4's filename-pattern first pass (docs/2608/260811/
// pre-chunking-action-items.md): "don't pay a model for what a filename already says."
// GGZ/GHZ/VVT/V&V/VGZ already appear verbatim in titles for the sector-specific CAOs and
// brochures, so a plain regex over the title is free and deterministic, unlike
// embedding-based clustering (FamilyIdEmbedder), which only ever gets a probabilistic
// answer.
public static partial class DomainTagger
{
    // One row per canonical sector tag. Surface variants (V&V for VVT) live inside that
    // tag's pattern rather than as extra rows: two rows mapping to one tag would need a
    // dedup guard in TagAll, and the next person adding an alias would forget it.
    private static readonly (string Tag, Regex Pattern)[] Patterns =
    [
        ("GGZ", GgzPattern()),
        ("GHZ", GhzPattern()),
        ("VGZ", VgzPattern()),
        ("VVT", VvtPattern()),
    ];

    // Letter-lookaround rather than \b: \w treats '_' as a word character, so \b wouldn't
    // find "GGZ" inside "GGZ_VGZ" (a real corpus title, see chunking-signals-map.md §2),
    // and "V&V" has no \b to anchor on around '&' at all. (?<!\p{L})...(?!\p{L}) only
    // requires non-letter neighbours, so '_', '&', start and end of string all count as a
    // boundary. \p{L} rather than [A-Za-z] because Dutch titles carry diacritics: "GGZé"
    // is a longer word, not a sector code.
    [GeneratedRegex(@"(?<!\p{L})GGZ(?!\p{L})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GgzPattern();

    // The only tag matched case-sensitively. "GHz" is also the SI unit, and a title like
    // "Handleiding 2.4 GHz koppeling" would otherwise be silently mislabelled as the
    // disability-care sector with nothing downstream able to catch it. The unit is
    // conventionally written with a lowercase z, so requiring uppercase separates the two in
    // ordinary prose. It does NOT help for an all-caps title ("2.4 GHZ DECT ADAPTER"), which
    // still false positives; that hole stays open because a digit guard would also reject
    // legitimate titles like "CAO 2024 GHZ". Case sensitivity also misses an all-lowercase
    // "cao ghz", which the corpus doesn't produce.
    [GeneratedRegex(@"(?<!\p{L})GHZ(?!\p{L})", RegexOptions.CultureInvariant)]
    private static partial Regex GhzPattern();

    [GeneratedRegex(@"(?<!\p{L})VGZ(?!\p{L})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VgzPattern();

    // V&V and VVT name the same sector, so both surface forms resolve to one canonical tag;
    // otherwise retrieval fragments across two buckets for what is one thing. The literal
    // form stays recoverable from the title. \s* around the ampersand so "V & V" reads the
    // same as "V&V". The trailing lookaround keeps "V&VN" (the professional association, not
    // a sector) from matching. The spelled-out "V en V" is deliberately not matched: it
    // doesn't occur in the corpus, and "V en V" style alternations would start colliding
    // with ordinary Dutch sentence text.
    [GeneratedRegex(@"(?<!\p{L})(?:VVT|V\s*&\s*V)(?!\p{L})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VvtPattern();

    // Every sector code the title names, in Patterns order (not order of appearance in the
    // title). Empty when none appear, which is the honest "unknown" rather than a guess.
    // Returning all of them keeps a comparison brochure naming both GGZ and GHZ from
    // silently losing one; callers that want a single value take the first.
    public static IReadOnlyList<string> TagAll(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return [];

        List<string> tags = [];
        foreach (var (tag, pattern) in Patterns)
            if (pattern.IsMatch(title))
                tags.Add(tag);

        return tags;
    }

    // Null when no known sector code appears in the title. When a title names more than one,
    // the winner is the one listed earliest in Patterns, NOT the one appearing earliest in
    // the title: "GHZ en GGZ" returns GGZ. See TagAll if that loss matters to the caller.
    public static string? Tag(string title)
    {
        var tags = TagAll(title);
        return tags.Count > 0 ? tags[0] : null;
    }
}
