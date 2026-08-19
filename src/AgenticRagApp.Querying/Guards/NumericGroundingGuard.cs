using System.Text.RegularExpressions;

namespace AgenticRagApp.Querying.Guards;

// Finds numeric claims in an answer that appear nowhere in the retrieved context.
//
// The failure this catches, measured in the 260818 eval: the model answered "8,33%
// vakantietoeslag" with [ref_id] citations attached while that figure appeared in NONE of the
// retrieved chunks - the correct number, recalled from pretraining, wearing a fabricated
// citation. Equivalence scored 5, Groundedness 1, and nothing failed. Three more scenarios
// carried "58,4" and "237,4" the same way. A right answer with an invented source is worse
// than a wrong one: it teaches the reader to trust citations that mean nothing.
//
// Deliberately detection, not correction: rewriting the model's Dutch prose to excise a
// sentence is how a fluent answer becomes a broken one. The querying service logs the finding;
// the eval records it per scenario as UngroundedNumbers, so the class fails a run visibly
// instead of hiding behind a 5 on Equivalence.
//
// What counts as a numeric claim: decimal numbers ("8,33", "58,4", "1.847,50"), percentages,
// and integers of 3+ digits (salary steps like "2649" matter; article numbers like "21" are
// noise). Matching tolerates the comma/point separator swap, nothing looser - a fuzzy number
// match would defeat the point.
public static partial class NumericGroundingGuard
{
    // Decimal forms first so "8,33%" is one token, not "8" + ",33%". Word boundaries keep
    // "2026-08" from bleeding digits across the dash.
    [GeneratedRegex(@"\d+(?:[.,]\d+)+\s?%|\d+(?:[.,]\d+)+|\d+\s?%|\d{3,}", RegexOptions.None)]
    private static partial Regex NumericLiteral();

    public static IReadOnlyList<string> FindUngrounded(string answer, string retrievedContext)
    {
        if (string.IsNullOrWhiteSpace(answer) || string.IsNullOrWhiteSpace(retrievedContext))
            return [];

        return NumericLiteral().Matches(answer)
            .Select(m => m.Value.Trim())
            .Distinct(StringComparer.Ordinal)
            .Where(number => !AppearsIn(retrievedContext, number))
            .ToList();
    }

    private static bool AppearsIn(string context, string number)
    {
        var bare = number.TrimEnd('%', ' ');

        // The literal, or its separator-swapped twin ("8,33" vs "8.33") - documents and
        // answers disagree about Dutch vs invariant formatting for the same value.
        return context.Contains(bare, StringComparison.Ordinal) ||
               context.Contains(SwapSeparators(bare), StringComparison.Ordinal);
    }

    private static string SwapSeparators(string number)
    {
        var chars = number.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            chars[i] = chars[i] switch { ',' => '.', '.' => ',', _ => chars[i] };

        return new string(chars);
    }
}
