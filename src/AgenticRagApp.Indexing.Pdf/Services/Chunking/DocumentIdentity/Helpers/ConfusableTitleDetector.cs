using System.Text.RegularExpressions;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Pre-chunking action item C3, lexically close but semantically distant (Medido/Medimo).
// Compares individual words across two titles rather than whole titles, since the confusable
// pair is usually one product-name-shaped word buried in otherwise-unrelated titles, not the
// titles overall: "Handleiding Medicijndispenser (Medido)" and "Handreiking Medimo voor
// zorgmedewerkers" are far apart as whole strings, and the one word that matters gets averaged
// away.
//
// Only checked against documents outside this document's own family: two titles in the same
// family aren't "confusable", they're already correctly grouped as the same thing.
//
// Split out of DocumentIdentityResolver with the clusterer, and for the same reason - this is pure
// string work that needed an embedding client and a blob store mocked to reach it. Matches are
// returned rather than logged so the caller can log them and, later, put them in the run report:
// knowing WHICH words collided is what makes the flags checkable by hand.
public static class ConfusableTitleDetector
{
    // Normalized word-level Levenshtein distance below which two *different* words across two
    // titles are flagged as confusable (Medido/Medimo territory).
    public const double ConfusableWordThreshold = 0.30;

    // Words shorter than this are skipped. Short Dutch function words ("van", "een", "de")
    // would otherwise swamp it with noise matches, and at length 4 a single edit already scores
    // 0.25, i.e. under the threshold, so any one-character difference would match ("Zorg" vs
    // "Zorn").
    public const int MinConfusableWordLength = 5;

    // Absolute edit-distance ceiling on top of the normalized ratio. Without it, long words pass
    // on ratio alone: two 20-character titles differing in 5 characters score 0.25.
    //
    // CALIBRATED 2026-08-14 against the full corpus (run 08:32, see
    // docs/2608/260814/calibration-findings.md §3). At 2 the check produced 44 matches, of which
    // 40 were noise and 4 were the case it exists for:
    //
    //   30x HANDREIKING/Handleiding   - two generic Dutch document-type words, distance 2
    //    6x werken/inwerken           - one word inside another, distance 2
    //    4x Infografic/Infographic    - Dutch/English spelling of one word, distance 2
    //    4x Medido/Medimo             - the real product-name collision, distance 1
    //
    // Every false positive sat at exactly 2 edits and the motivating pair at 1, so lowering the
    // ceiling to 1 removes all 40 and keeps all 4. What this encodes about this corpus: two
    // edits is always coincidence here, one edit is the product-name case. If a genuine
    // two-edit collision ever appears, this is the constant to revisit - the run report lists
    // every match with the words that caused it, so the evidence will be in hand.
    public const int MaxConfusableEdits = 1;

    private static readonly Regex WordPattern = new(@"[\p{L}\p{Nd}]+", RegexOptions.Compiled);

    // Confusable candidates must be all-letter tokens. Numeric and alphanumeric tokens are the
    // dominant false-positive source in this corpus: "2024" vs "2025" scores 0.25 and would flag
    // every year, version number and article code as a confusable pair. Note this filters whole
    // tokens produced by WordPattern, which deliberately keeps letters and digits together -
    // tokenizing on letters alone would split "Medido2024" into a bare "Medido" and reintroduce
    // exactly the matches this is meant to suppress.
    private static readonly Regex LettersOnly = new(@"^\p{L}+$", RegexOptions.Compiled);

    // thisRunSourceIds: only documents in the current run get an entry, so the relation is
    // one-directional against older documents - an older document confusable with one being
    // processed now does not get the flag until it is itself reindexed (see DocumentIdentityResolver).
    public static ConfusableResult Detect(
        IReadOnlyList<string>               thisRunSourceIds,
        IReadOnlyDictionary<string, string> titlesById,
        IReadOnlyDictionary<string, string> familyIdOf)
    {
        var confusableOf = new Dictionary<string, IReadOnlyList<string>>();
        var matches      = new List<ConfusableMatch>();

        // Tokenized once per document rather than once per comparison. The inner loop runs over
        // the whole corpus for every document in the run, so re-running the regex there is the
        // difference between n and n^2 tokenizations.
        var wordsOf = titlesById.ToDictionary(kv => kv.Key, kv => ConfusableWords(kv.Value));

        foreach (var sourceId in thisRunSourceIds)
        {
            // familyIdOf, titlesById and wordsOf all carry the same key set, so this one guard
            // covers the lookups below.
            if (!familyIdOf.TryGetValue(sourceId, out var ownFamily))
                continue;

            var ownWords = wordsOf[sourceId];
            if (ownWords.Count == 0)
                continue;

            var confusable = new List<string>();

            foreach (var otherId in titlesById.Keys)
            {
                if (otherId == sourceId || familyIdOf.GetValueOrDefault(otherId) == ownFamily)
                    continue;

                var match = FirstConfusablePair(ownWords, wordsOf[otherId]);
                if (match is null) continue;

                confusable.Add(otherId);
                matches.Add(new ConfusableMatch(sourceId, otherId, match.Value.Own, match.Value.Other));
            }

            // Ordinal sort so the persisted value is stable run to run - the same determinism
            // argument the FamilyId choice already follows. Without it the order is whatever
            // the comparison set happened to enumerate in.
            if (confusable.Count > 0)
                confusableOf[sourceId] = confusable.OrderBy(id => id, StringComparer.Ordinal).ToList();
        }

        return new ConfusableResult(confusableOf, matches);
    }

    // Returns the first colliding word pair rather than a bool: the SourceIds alone say two
    // documents are confusable but not why, and "why" is what a human checking the flags needs.
    private static (string Own, string Other)? FirstConfusablePair(
        IReadOnlyList<string> ownWords, IReadOnlyList<string> otherWords)
    {
        foreach (var own in ownWords)
            foreach (var other in otherWords)
                if (IsConfusablePair(own, other))
                    return (own, other);

        return null;
    }

    private static List<string> ConfusableWords(string? title) =>
        WordPattern.Matches(title ?? string.Empty)
            .Select(m => m.Value)
            .Where(w => w.Length >= MinConfusableWordLength && LettersOnly.IsMatch(w))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    // Both the ratio and the absolute edit count have to be small. The ratio alone lets long
    // words through on several edits; the absolute cap alone flags every short word pair.
    private static bool IsConfusablePair(string a, string b)
    {
        // Identical words are not confusable, they are the same word - "Handleiding" appears in
        // half the corpus's titles and would otherwise pair every manual with every other at
        // distance 0.
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            return false;

        // One word wholly contained in the other, at either end. This is inflection and
        // compounding, not confusion: "Handleiding"/"Handleidingen" (plural, 2 edits) and
        // "werken"/"inwerken" (compound, 2 edits, measured 6 times in the corpus) are the same
        // word, and flagging them re-creates the identical-word noise the equality check above
        // exists to stop.
        //
        // Both ends are checked, not just the prefix: "werken" is a SUFFIX of "inwerken", so a
        // prefix-only guard missed it - found in the 2026-08-14 run report.
        //
        // Medido/Medimo differ at the fifth character, so neither contains the other and the
        // motivating case survives. The known cost is a genuine collision that happens to be an
        // affix of the other word; this corpus contains none.
        if (a.StartsWith(b, StringComparison.OrdinalIgnoreCase) ||
            b.StartsWith(a, StringComparison.OrdinalIgnoreCase) ||
            a.EndsWith(b, StringComparison.OrdinalIgnoreCase)   ||
            b.EndsWith(a, StringComparison.OrdinalIgnoreCase))
            return false;

        // Cheap length prefilter before the O(n*m) matrix: the length difference is itself a
        // lower bound on the edit distance.
        if (Math.Abs(a.Length - b.Length) > MaxConfusableEdits)
            return false;

        var distance = LevenshteinDistance(a, b);
        if (distance > MaxConfusableEdits)
            return false;

        return NormalizedLevenshtein(a, b, distance) <= ConfusableWordThreshold;
    }

    private static double NormalizedLevenshtein(string a, string b, int distance)
    {
        var maxLen = Math.Max(a.Length, b.Length);
        return maxLen == 0 ? 0 : (double)distance / maxLen;
    }

    // Two-row variant: the full matrix is never needed, only the distance.
    private static int LevenshteinDistance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var previous = new int[b.Length + 1];
        var current  = new int[b.Length + 1];

        for (int j = 0; j <= b.Length; j++) previous[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var ca = char.ToLowerInvariant(a[i - 1]);

            for (int j = 1; j <= b.Length; j++)
            {
                var cost = ca == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}

public sealed record ConfusableMatch(string SourceId, string OtherSourceId, string Word, string OtherWord);

public sealed record ConfusableResult(
    IReadOnlyDictionary<string, IReadOnlyList<string>> ConfusableOf,
    IReadOnlyList<ConfusableMatch>                     Matches);
