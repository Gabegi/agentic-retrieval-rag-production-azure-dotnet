using System.Text.RegularExpressions;
using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Utils;

// Builds each heading's ancestor chain ("Hoofdstuk 3 > 3.2 Dosering") from Document
// Intelligence's own nested section spans.
//
// Why the section tree rather than Heading.Depth: Phase A measured that section starts and
// heading offsets coincide EXACTLY (99.4-100%, both directions, at tolerance 0) on all four
// big documents, and that DI's sections nest - each carries one span, and span lengths sum to
// ~1.3M against a ~346k document, with the largest covering the whole file. So the section
// list is a flattened tree, and containment gives the hierarchy directly.
//
// That matters because it needs nothing from Heading.Depth, which is unverified: the cached
// corpus JSON predates that field, so nothing has yet checked whether DI's rendered "#" levels
// are reliable on this corpus. Containment is measured; depth is assumed.
//
// Everything here works in RAW offsets. Both inputs - section spans and heading offsets - are
// raw-content coordinates, so they are directly comparable to each other even though neither
// is comparable to the cleaned text the chunker cuts.
public static partial class HeadingChainBuilder
{
    // ── Sibling and vacant filtering ────────────────────────────────────────
    //
    // Containment says WHERE a section sits; it says nothing about whether DI drew its span
    // correctly. Measured on the 260818 run, DI reliably over-extends a section around empty
    // "(vacant)" articles, so the preceding sibling swallows the articles after it and the
    // chain reads "Artikel 1 de arbeidsovereenkomst > Artikel 2 duur van de
    // arbeidsovereenkomst" - 479 of 2,963 heading paths carried two or more Artikel levels,
    // and 65 carried a "(vacant)" segment. Two shape rules repair what containment gets wrong:
    //
    //   1. An ancestor at the SAME structural level as the leaf is a sibling, whatever the
    //      spans say. "Artikel 14" does not contain "Artikel 16"; "Hoofdstuk 2" does not
    //      contain "Hoofdstuk 3"; "3.2 Dosering" does not contain "3.4 Bewaren". Levels that
    //      genuinely nest - "Hoofdstuk 3" over "Artikel 3:5", "1. Inleiding" over "1.1
    //      Doelstelling" - have different shapes and pass.
    //
    //   2. A "(vacant)" heading is never an ancestor. It has no content, so nothing can be
    //      under it; its only effect was to displace a real parent from the capped chain
    //      (PrefixBuilder keeps the LAST three levels, so every false segment evicts a true
    //      one from the other end).
    //
    //   3. A document-spanning, shapeless, SLOGAN-STYLED section is COVER FURNITURE, not a
    //      parent. DI opens a section on the cover page and runs its span to the end of the
    //      file, so the CAO VVT cover slogan "De client centraal DE MEDEWERKER OP EEN!" became
    //      the root of every VVT breadcrumb in the 260818 run - repeated into every VVT chunk's
    //      embedded text, diluting every VVT vector for nothing.
    //
    //      THREE conditions, all required, and the third is the one that earns its place.
    //      Spanning-and-shapeless alone is not enough: "Contoso Privacybeleid" and "Inleiding"
    //      are both spanning, both shapeless, and both genuine parents. Nothing structural
    //      separates a cover slogan from a document title used as a root - so the separator is
    //      typographic, which is what a slogan actually is. Shouting (a run of ALL-CAPS words)
    //      or an exclamation mark is a design choice no section heading in this corpus makes.
    //
    //      The direction is deliberate, as with TocFilter: a slogan left in a breadcrumb costs
    //      tokens, a real parent struck from one costs the chain its meaning and cannot be
    //      recovered downstream. When the signals disagree, the ancestor stays.

    [GeneratedRegex(@"^artikel\s+\d+\w*(:\d+\w*)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex ArtikelShape();

    [GeneratedRegex(@"^hoofdstuk\s+\d+\w*\b", RegexOptions.IgnoreCase)]
    private static partial Regex HoofdstukShape();

    [GeneratedRegex(@"^bijlage\s+\S+", RegexOptions.IgnoreCase)]
    private static partial Regex BijlageShape();

    // Dotted numbering: "3. Verantwoordelijkheden", "4.14. De Ondernemingsraad", "3.3 Wat
    // moet je doen". The segment COUNT is the level - "1." and "3." are siblings, "1." and
    // "1.1." are not. A single number qualifies only WITH its trailing dot: "2024 Jaarplan"
    // and "45 minuten pauze" open with a number but are not numbered headings, and treating
    // them as dotted-1 discarded their genuine "N." parents as siblings (code-review finding
    // 260818). Multi-segment forms need no trailing dot - "3.3 X" is unambiguous.
    [GeneratedRegex(@"^(?:(\d+(?:\.\d+)+)\.?|(\d+)\.)\s", RegexOptions.None)]
    private static partial Regex DottedShape();

    [GeneratedRegex(@"\(\s*vacant\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex Vacant();

    // A structural-level key, or null for a heading with no recognisable numbering shape.
    // Null never equals null here: two unshaped headings ("Inleiding" containing
    // "Definities") tell us nothing about levels, and the containment verdict stands.
    private static string? StructuralLevel(string title)
    {
        if (ArtikelShape().IsMatch(title))   return "artikel";
        if (HoofdstukShape().IsMatch(title)) return "hoofdstuk";
        if (BijlageShape().IsMatch(title))   return "bijlage";

        var dotted = DottedShape().Match(title);
        if (dotted.Success)
        {
            // Group 1: multi-segment ("3.3", "4.14"). Group 2: single number with its dot.
            var number = dotted.Groups[1].Success ? dotted.Groups[1].Value : dotted.Groups[2].Value;

            return $"dotted-{number.Count(c => c == '.') + 1}";
        }

        return null;
    }

    // Public because HeadingLocator's paired zero-body merge asks the same question this
    // class was built to answer, and the two must agree. That merge exists for a heading and
    // its CONTINUATION ("3.3 Wat moet je doen" followed by "Acties als..."), where folding
    // the second line into the first's title is right. Two headings at the same structural
    // level are not that: they are separate sections that happen to leave the first one
    // empty, and folding them produces a single segment naming both - "Artikel 1:6 (vacant)
    // Artikel 1:7 Toepassing CAO op relatiepartner", which then reads as two Artikel levels
    // to anything measuring the path, carries a vacant article's number into a real article's
    // identity, and cannot be undone downstream because the two titles are now one string.
    //
    // GetHeadingsHelper's BareNumberedLabelWithWord gate already refused this for the bare
    // "Artikel 8" / "Artikel 9" case; this is the same judgement made on shape rather than on
    // whether the label happens to be bare. 408 of the 260819 run's paths still carried two
    // Artikel levels and 13 still carried "(vacant)" for want of it.
    public static bool AreSameStructuralLevel(string first, string second)
    {
        var level = StructuralLevel(first);

        return level is not null && level == StructuralLevel(second);
    }

    private static bool IsSibling(string ancestor, string leaf) => AreSameStructuralLevel(ancestor, leaf);

    // Rule 3's span test. 0.9 rather than 1.0 because DI's cover section starts at the first
    // rendered glyph and ends at the last, which is a hair short of the document on both ends;
    // and far enough above any real chapter that a document would have to be almost entirely
    // one chapter to trip it - in which case that chapter's title is the document's title
    // anyway, and PrefixBuilder's TitleLine already carries it.
    private const double CoverSpanShare = 0.9;

    // THREE or more consecutive shouted words. Three, not two, because this corpus's document
    // titles ARE two shouted words - "CAO GGZ", "CAO VVT", "CAO GHZ" - and a two-word rule
    // would strike the very roots most worth keeping. "DE MEDEWERKER OP EEN" is four.
    // \p{Lu} rather than ToUpper equality so "EEN" counts and "Een" does not.
    [GeneratedRegex(@"\p{Lu}{2,}(\s+\p{Lu}{2,}){2,}")]
    private static partial Regex ShoutedRun();

    // Slogan typography: shouting, or an exclamation mark. Neither appears in a genuine section
    // heading anywhere in this corpus.
    private static bool IsSloganStyled(string title) =>
        title.Contains('!') || ShoutedRun().IsMatch(title);

    // Cover furniture: spans (nearly) the whole document, carries no structural numbering, AND
    // is styled as a slogan. documentLength is the widest extent seen, not doc.Content.Length -
    // these are RAW coordinates and the cleaned content is a different, shorter space (see the
    // class note).
    private static bool IsCoverFurniture(string title, int start, int end, int documentLength) =>
        documentLength > 0
        && (end - start) >= documentLength * CoverSpanShare
        && StructuralLevel(title) is null
        && IsSloganStyled(title);

    // Ancestor titles, outermost first, for each heading offset. A heading with no enclosing
    // section returns an empty chain rather than being omitted - "top level" and "no data" are
    // different answers, and the caller distinguishes them by whether the heading is present.
    public static IReadOnlyDictionary<int, IReadOnlyList<string>> Build(
        IReadOnlyList<SectionInfo> sections, IReadOnlyList<Heading> headings)
    {
        var chains = new Dictionary<int, IReadOnlyList<string>>();
        if (headings.Count == 0) return chains;

        // Each section, reduced to its extent plus the heading that opens it. The join is by
        // exact offset equality, which Phase A showed holds - a section whose start matches no
        // heading (the document-spanning root, typically) contributes extent but no title.
        // Ancestor titles take the same shape as the leaf - every line, space-joined - so a path
        // is not half flattened and half first-line-only. This took the first line and dropped
        // the rest, which rendered an extraction-merged ancestor as "Artikel 9" in a chain whose
        // leaf said "Artikel 9 Begrippen".
        var headingAt = headings
            .Where(h => h.Offset.HasValue)
            .GroupBy(h => h.Offset!.Value)
            .ToDictionary(g => g.Key, g => HeadingTextNormalizer.Flatten(g.First().Content) ?? "");

        var extents = sections
            .SelectMany(s => s.Spans.Select(sp => (Start: sp.Offset, End: sp.Offset + sp.Length)))
            .Select(e => (e.Start, e.End, Title: headingAt.GetValueOrDefault(e.Start)))
            .ToList();

        // Rule 3's yardstick: the widest span DI drew, which for these documents IS the
        // document - the class note records span lengths summing to ~1.3M against a ~346k
        // file, with the largest covering the whole of it.
        var documentLength = extents.Count == 0 ? 0 : extents.Max(e => e.End - e.Start);

        foreach (var heading in headings.Where(h => h.Offset.HasValue))
        {
            var offset = heading.Offset!.Value;
            var leaf   = HeadingTextNormalizer.Flatten(heading.Content) ?? "";

            // Ancestors are the sections that strictly contain this heading: they open before
            // it and close at or after it. "Strictly" excludes the heading's own section,
            // which would otherwise appear as its own ancestor. Containment is then filtered
            // by the three shape rules above - a sibling-shaped, "(vacant)" or cover-furniture
            // title is a span error, not a parent.
            var ancestors = extents
                .Where(e => e.Start < offset && e.End >= offset && e.Title is not null)
                .Where(e => !Vacant().IsMatch(e.Title!) && !IsSibling(e.Title!, leaf))
                .Where(e => !IsCoverFurniture(e.Title!, e.Start, e.End, documentLength))
                // Outermost first: the widest enclosing section is the top of the chain.
                .OrderByDescending(e => e.End - e.Start)
                .Select(e => e.Title!)
                .ToList();

            chains[offset] = ancestors;
        }

        return chains;
    }

    // The full path for one heading: its ancestors, then itself.
    public static string? Path(
        IReadOnlyDictionary<int, IReadOnlyList<string>> chains, int? offset, string? ownHeading)
    {
        if (string.IsNullOrWhiteSpace(ownHeading)) return null;

        var own = HeadingTextNormalizer.Flatten(ownHeading) ?? "";
        if (offset is null || !chains.TryGetValue(offset.Value, out var ancestors) || ancestors.Count == 0)
            return own;

        // Duplicates are dropped rather than repeated: a section whose span starts at its own
        // heading can appear both as an ancestor and as the leaf depending on how DI nested
        // it, and "Hoofdstuk 3 > Hoofdstuk 3" reads as a structure error to anyone seeing it
        // in a citation.
        var parts = ancestors.Append(own)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return string.Join(" > ", parts);
    }
}
