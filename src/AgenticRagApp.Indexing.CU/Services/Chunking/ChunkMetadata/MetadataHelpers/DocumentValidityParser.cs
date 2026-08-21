using System.Text.RegularExpressions;

namespace AgenticRagApp.Indexing.CU.Services;

// The validity period and version a document declares IN ITS OWN TITLE.
//
// Worth having because this corpus puts it there and nowhere else: "CAO GGZ 2024 2026" is the
// only machine-readable statement that the document stopped applying at the end of 2026. The
// retrieval failure it addresses is a confident answer quoted from a superseded CAO, which no
// similarity score can flag - the same shape of failure domain_tag exists to prevent, on the
// time axis instead of the sector axis.
//
// TITLE ONLY, deliberately. Effective dates written in a clause body ("vanaf 1 juli 2025") are
// scope 3, a property of the CONTENT - a different field with a different meaning, and stamping
// one onto the whole document would be wrong.
public static class DocumentValidityParser
{
    // "v2", "v2.1", "versie 3" - the whole match is removed before years are read, so a version
    // never contributes a digit run to the period.
    //
    // The abbreviated form requires the digits to follow "v" IMMEDIATELY - no dot, no space.
    // Allowing a gap made this match the tail of any Dutch abbreviation ending in v: "t.o.v.
    // 2024" parsed as version "2024", the year vanished from the title, and a two-year title
    // came back as a one-year one. Only the spelled-out "versie" may be separated from its
    // number, because nothing else ends in that word.
    private static readonly Regex VersionToken =
        new(@"\b(?:versie\.?\s*|v)(\d+(?:\.\d+)*)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Bare four-digit years. Bounded to a plausible document range so a house number, an
    // amount or an article number cannot be read as one.
    private static readonly Regex Year =
        new(@"\b(19\d{2}|20\d{2})\b", RegexOptions.Compiled);

    private const int MinYear = 1900;
    private const int MaxYear = 2100;

    public static DocumentValidity Parse(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return DocumentValidity.None;

        var versionMatch = VersionToken.Match(title);
        var version      = versionMatch.Success ? versionMatch.Groups[1].Value : null;

        // Read years from the title with the version token removed, so "CAO 2024 v2.1" cannot
        // read 2 and 1 as anything and "Protocol v2024" cannot be read as a year at all.
        var withoutVersion = versionMatch.Success ? title.Remove(versionMatch.Index, versionMatch.Length) : title;

        var years = Year.Matches(withoutVersion)
            .Select(m => int.Parse(m.Groups[1].Value))
            .Where(y => y is >= MinYear and <= MaxYear)
            .ToList();

        return years switch
        {
            // No year: the common case for policy documents, which carry their dates in blob
            // metadata instead. Null is "the title did not say", never "valid forever".
            []           => new DocumentValidity(null, null, version),

            // One year opens a period without closing it. Do NOT infer the end of that year -
            // "CAO GGZ 2024" is a document that STARTED in 2024, and stamping 2024-12-31 as
            // valid_to would expire it on a date it never claimed.
            [var only]   => new DocumentValidity(StartOf(only), null, version),

            // Two or more: first and last, in the order the title wrote them. Anything beyond
            // the first pair is a year mentioned in the subject rather than a bound.
            _ when years[^1] >= years[0]
                         => new DocumentValidity(StartOf(years[0]), EndOf(years[^1]), version),

            // Descending years are not a period. A title like "Wijziging 2026 t.o.v. 2024"
            // mentions both without declaring either as a bound, so claim neither.
            _            => new DocumentValidity(null, null, version),
        };
    }

    // A bare year is normalized to a real instant so Search range filters work at all - a
    // string year cannot answer "which documents were in force on this date". The precision it
    // implies is not in the title: a CAO published mid-year still stamps 1 January. Lossy on
    // purpose, and only ever as precise as the title was.
    private static DateTimeOffset StartOf(int year) => new(year, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset EndOf(int year) => new(year, 12, 31, 23, 59, 59, TimeSpan.Zero);
}

// What a title declared about when the document applies. All three null is the normal case.
public sealed record DocumentValidity(DateTimeOffset? From, DateTimeOffset? To, string? Version)
{
    public static readonly DocumentValidity None = new(null, null, null);
}
