using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Services;

namespace AgenticRagApp.Indexing.CU.Utils;

// One heading section of a document, in markdown coordinates - the same string chunking cuts.
// Body is the text between this heading and the next one - which is what gets cut, not the
// heading line itself.
public sealed record LocatedSection(
    int      Index,
    string?  HeadingText,
    string?  HeadingPath,
    string   HeadingSource,
    int      Depth,
    int      Start,
    int      End,
    int      PageNumber,
    bool     Located)
{
    public int Length => End - Start;
}

public sealed record HeadingLocationResult(
    IReadOnlyList<LocatedSection> Sections,
    int                           HeadingsTotal,
    int                           HeadingsLocated,
    int                           PairedHeadingsMerged,

    // Headings that arrived with no span at all. Such a heading cannot open a section - there
    // is no position to open it at, and inventing one (the previous locator inherited the last
    // offset seen) is a guess about where in the document it sits. It is counted and NOT
    // located: zero on every document measured so far (0 of 1,273 across the big four), so a
    // nonzero value here is an extraction anomaly worth reporting, not an input worth absorbing.
    //
    // Reported for the same reason as the three counters above: the caller needs it even
    // when the document goes on to produce no chunks at all.
    int                           HeadingsWithoutOffset)
{
    // Share of headings that could not open a section: no offset, or an offset outside the
    // content. Both mean the service's own span disagrees with the service's own markdown,
    // which is not a chunking result but an upstream one - CUHelper warns per document when
    // its span check fails, and this is the run-level form of the same measurement.
    public double FailureRate => HeadingsTotal == 0 ? 0 : 1 - (HeadingsLocated / (double)HeadingsTotal);
}

// Turns Content Understanding's detected headings into section boundaries: the cut for a
// heading is its own span offset, directly (2026-09-09).
//
// This class used to re-find every heading by string search - normalize the text, search
// within the heading's page window from a moving cursor, then walk back over the "##" marker
// the match had landed after. That existed for Document Intelligence, whose offsets addressed
// RAW content while chunking cut CLEANED content (PdfCleaner drifted length by a measured
// 1.066-1.202x). Neither half of that premise survives the CU switch: nothing cleans the
// markdown any more (CUHelper returns it verbatim), the heading span is a utf16 index into
// exactly that string, and the cu-raw-response capture shows the span STARTS AT THE MARKER
// ("# Wifi uitzetten..." at offset 0, length 56, content 54 chars) - so the walk-back was
// reconstructing a position the service had already reported. Three runs x 51 documents on
// 2026-08-27 produced zero span-misalignment warnings from CUHelper's check. D146 round 2
// deferred span-direct addressing "until Step 0 shows span reliability"; that is the evidence.
//
// What remains here is chunking POLICY, none of it a re-derivation of service data: the
// preamble section and the paired zero-body merge (with its two refusals). TableCaptionSplitter
// - a caption line above a GFM table promoted to a boundary - went 2026-09-09: dead under the
// HTML tables CU emits (its row test never matched), and the CAO GHZ salary tables it was
// written for carry neither a caption line nor a typed Caption in the CU output.
public static class HeadingLocator
{
    public static HeadingLocationResult Locate(
        string content,
        IReadOnlyList<Heading> headings,
        IReadOnlyList<PageSpan> pageSpans,
        IReadOnlyList<SectionInfo>? sections = null)
    {
        if (string.IsNullOrEmpty(content))
            return new HeadingLocationResult([], headings.Count, 0, 0, 0);

        var found      = new List<(Heading Heading, int At)>();
        var offsetless = 0;

        foreach (var heading in headings)
        {
            // A null offset means the paragraph carried no span - explicitly not 0, since 0 is a
            // real offset and cannot double as "unknown". No position, no section.
            if (heading.Offset is not { } at)
            {
                offsetless++;
                continue;
            }

            // An offset past the end of the content is the service contradicting itself (the
            // span addresses a string the markdown is not). Not located, and the failure rate
            // shows it - never clamped to the end, which would open an empty section there.
            if (at < 0 || at >= content.Length) continue;

            found.Add((heading, at));
        }

        // Reading order is offset order, and offsets are positions in the string being cut -
        // the sort is stable, so two headings the service placed at one offset keep arrival order.
        found = [.. found.OrderBy(f => f.At)];

        // Ancestor chains come from CU's nested section spans, not from Heading.Depth -
        // containment is measured, depth is assumed. See HeadingChainBuilder.
        var chains = HeadingChainBuilder.Build(sections ?? [], headings);

        var (built, merged) = BuildSections(content, found, pageSpans, chains);

        return new HeadingLocationResult(
            built, headings.Count, found.Count, merged, offsetless);
    }

    // A heading paragraph is one line in CU's output; FirstLine is kept for the length
    // comparison below so a Content that does carry a newline compares its heading LINE, which
    // is what the body slice would start with.
    private static string FirstLine(string content) =>
        content.Split('\n')[0].Trim();

    // Turns located headings into contiguous sections, applying the two rules the cascade
    // was missing.
    private static (List<LocatedSection> Sections, int Merged) BuildSections(
        string content,
        List<(Heading Heading, int At)> found,
        IReadOnlyList<PageSpan> pageSpans,
        IReadOnlyDictionary<int, IReadOnlyList<string>> chains)
    {
        var sections = new List<LocatedSection>();
        var merged   = 0;
        var index    = 0;

        // Rule 1 - preamble. Content before the first heading is its own section, not
        // merged into the first one. Merging would attribute frontmatter, a cover page or a
        // table of contents to a section it is not part of, and that misattribution rides
        // into the embedded text as a heading prefix that is simply wrong.
        var firstStart = found.Count > 0 ? found[0].At : content.Length;
        if (firstStart > 0 && content[..firstStart].Trim().Length > 0)
        {
            sections.Add(new LocatedSection(
                Index: index++, HeadingText: null, HeadingPath: null,
                HeadingSource: ChunkHeadingSource.None,
                Depth: 0, Start: 0, End: firstStart,
                PageNumber: PageAt(pageSpans, 0), Located: true));
        }

        for (var i = 0; i < found.Count; i++)
        {
            var (heading, at) = found[i];
            var end = i + 1 < found.Count ? found[i + 1].At : content.Length;

            // Rule 2 - paired zero-body headings. Hygienecode emits pairs like
            // "3.3 X: wat moet je doen..." immediately followed by "Acties als...", where
            // the first has no body between it and the second. Left alone each pair produces
            // a near-empty parent whose only content is its own heading line. The pair is
            // merged into one section: the second heading's text is folded into the first's,
            // and the section runs to wherever the second would have ended.
            //
            // The section starts at the heading's span, which includes its markdown marker, so
            // the marker is stripped before the zero-body length comparison - otherwise a
            // marker'd pair reads as having "### " of body and the merge stops firing.
            var body        = content[at..end].Trim().TrimStart('#').TrimStart();
            var headingLine = FirstLine(heading.Content);

            // Two refusals, and they must agree with each other and with the shape rules in
            // HeadingChainBuilder. A bare numbered label ("Artikel 8") followed by another
            // heading is two short articles, not a pair (BareNumberedLabelWithWord); and two
            // headings at the same structural level are siblings whatever the body length says
            // (AreSameStructuralLevel). The vacant articles this stops merging become
            // heading-only sections, which is what they are - the residue filter, not this
            // merge, is the right place to drop them.
            var isBareLabel = HeadingNumbering.BareNumberedLabelWithWord()
                                               .IsMatch(FirstLine(heading.Content));

            var nextIsSibling = i + 1 < found.Count
                && HeadingChainBuilder.AreSameStructuralLevel(
                       HeadingTextNormalizer.Flatten(heading.Content) ?? "",
                       HeadingTextNormalizer.Flatten(found[i + 1].Heading.Content) ?? "");

            if (!isBareLabel && !nextIsSibling && body.Length <= headingLine.Length + 2 && i + 1 < found.Count)
            {
                var (next, _) = found[i + 1];
                var nextEnd = i + 2 < found.Count ? found[i + 2].At : content.Length;

                // Every line of both headings, space-joined - see HeadingTextNormalizer.
                var mergedHeading = string.Join(' ',
                    new[] { HeadingTextNormalizer.Flatten(heading.Content),
                            HeadingTextNormalizer.Flatten(next.Content) }
                        .Where(part => !string.IsNullOrWhiteSpace(part)));

                sections.Add(new LocatedSection(
                    Index: index++,
                    HeadingText: mergedHeading,
                    // The chain is the FIRST heading's - the pair is one section opened by
                    // that heading, and the second line is a continuation of its title, not a
                    // level below it.
                    HeadingPath: HeadingChainBuilder.Path(chains, heading.Offset, mergedHeading),
                    HeadingSource: ChunkHeadingSource.DiHeading,
                    Depth: heading.Depth,
                    Start: at, End: nextEnd,
                    PageNumber: heading.PageNumber, Located: true));

                merged++;
                i++;                     // the second heading is consumed by the merge
                continue;
            }

            var headingText = HeadingTextNormalizer.Flatten(heading.Content);

            sections.Add(new LocatedSection(
                Index: index++,
                HeadingText: headingText,
                HeadingPath: HeadingChainBuilder.Path(chains, heading.Offset, headingText),
                HeadingSource: ChunkHeadingSource.DiHeading,
                Depth: heading.Depth,
                Start: at, End: end,
                PageNumber: heading.PageNumber, Located: true));
        }

        // No headings anywhere: the whole document is one section. Branch 5 of the cascade
        // falls out of this rather than needing a route of its own.
        if (sections.Count == 0 && content.Trim().Length > 0)
        {
            sections.Add(new LocatedSection(
                Index: 0, HeadingText: null, HeadingPath: null,
                HeadingSource: ChunkHeadingSource.None,
                Depth: 0, Start: 0, End: content.Length,
                PageNumber: PageAt(pageSpans, 0), Located: true));
        }

        return (sections, merged);
    }

    // The page whose reported span CONTAINS the offset; 0 ("unknown") otherwise - the same
    // answer CuPageHelper.PageAt and PageResolver give, never the first span as a guess.
    private static int PageAt(IReadOnlyList<PageSpan> spans, int offset)
    {
        foreach (var s in spans)
            if (offset >= s.Offset && offset < s.Offset + s.Length)
                return s.PageNumber;

        return 0;
    }
}
