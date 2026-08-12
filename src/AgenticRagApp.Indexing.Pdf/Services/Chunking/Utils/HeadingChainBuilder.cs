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
public static class HeadingChainBuilder
{
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
        var headingAt = headings
            .Where(h => h.Offset.HasValue)
            .GroupBy(h => h.Offset!.Value)
            .ToDictionary(g => g.Key, g => g.First().Content.Split('\n')[0].Trim());

        var extents = sections
            .SelectMany(s => s.Spans.Select(sp => (Start: sp.Offset, End: sp.Offset + sp.Length)))
            .Select(e => (e.Start, e.End, Title: headingAt.GetValueOrDefault(e.Start)))
            .ToList();

        foreach (var heading in headings.Where(h => h.Offset.HasValue))
        {
            var offset = heading.Offset!.Value;

            // Ancestors are the sections that strictly contain this heading: they open before
            // it and close at or after it. "Strictly" excludes the heading's own section,
            // which would otherwise appear as its own ancestor.
            var ancestors = extents
                .Where(e => e.Start < offset && e.End >= offset && e.Title is not null)
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

        var own = ownHeading.Split('\n')[0].Trim();
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
