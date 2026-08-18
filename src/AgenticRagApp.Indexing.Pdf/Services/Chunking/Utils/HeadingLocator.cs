using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Utils;

// One heading section of a document, in CLEANED-content coordinates.
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

    // Headings that arrived with no DI offset at all, and so had to be ordered by arrival
    // position rather than by a measured one - see OrderByOffset. Zero on every document
    // measured so far (0 of 1,273 across the big four), which is exactly why it is counted
    // rather than assumed: a nonzero value here means extraction handed us a heading whose
    // paragraph carried no spans, and the section boundary it opens rests on a fallback.
    //
    // Reported for the same reason as the three counters above: the caller needs it even
    // when the document goes on to produce no chunks at all.
    int                           HeadingsWithoutOffset)
{
    // Share of headings that could not be placed in the cleaned text. This is the permanent
    // form of the measurement that chose this approach over rewriting PdfCleaner: Phase A
    // measured 1,273/1,273 exact matches across the big four, and the escalation rule was
    // fixed in advance at >2% corpus-wide (or >5% on any single document). If this metric
    // starts moving, that decision is due to be reopened - it is not a curiosity.
    public double FailureRate => HeadingsTotal == 0 ? 0 : 1 - (HeadingsLocated / (double)HeadingsTotal);
}

// Locates DI's detected headings inside a document's CLEANED text, and turns them into
// section boundaries (action-plan.md C6, and C5's two missing cascade rules).
//
// Why not just use Heading.Offset: those offsets address Document Intelligence's RAW
// content, while everything downstream consumes cleaned content. PdfCleaner changes length
// in nine separate ways - control and invisible characters removed, ligatures expanded,
// markdown escapes unescaped, NFC normalisation, hyphenation repair, three whitespace
// collapses, per-page trim - plus table HTML rewritten to pipe markdown and figures reduced
// to a caption or deleted. Phase A measured the resulting raw/cleaned ratio at 1.066-1.202
// across the big four, and the drift accumulates down the document. Slicing cleaned text at
// a raw offset cuts in the wrong place, further wrong the further in you go.
//
// So: PageNumber narrows the search to one page, a string match finds the real position,
// and Offset is used only to ORDER headings - which is what it is reliable for and what
// PageNumber cannot do (two headings on the same page are indistinguishable by page).
public static class HeadingLocator
{
    public static HeadingLocationResult Locate(
        string content,
        IReadOnlyList<Heading> headings,
        IReadOnlyList<PageSpan> pageSpans,
        IReadOnlyList<SectionInfo>? diSections = null)
    {
        if (string.IsNullOrEmpty(content))
            return new HeadingLocationResult([], headings.Count, 0, 0, 0);

        var (ordered, offsetless) = OrderByOffset(headings);

        var located = new List<(Heading Heading, int At, bool Found)>();
        var cursor  = 0;

        foreach (var heading in ordered)
        {
            var at = FindInPage(content, heading, pageSpans, cursor);
            if (at >= 0)
            {
                located.Add((heading, at, true));
                cursor = at + 1;
            }
            else
            {
                located.Add((heading, -1, false));
            }
        }

        var found  = located.Where(l => l.Found).OrderBy(l => l.At).ToList();

        // Ancestor chains come from DI's nested section spans, not from Heading.Depth -
        // containment is measured, depth is assumed. See HeadingChainBuilder.
        var chains = HeadingChainBuilder.Build(diSections ?? [], headings);

        var (sections, merged) = BuildSections(content, found, pageSpans, chains);

        return new HeadingLocationResult(
            sections, headings.Count, found.Count, merged, offsetless.Count);
    }

    // Reading order, plus the headings that could not state their own position.
    //
    // Offset is DI's raw-content offset, and it is used ONLY to order - never to slice. That
    // split is the whole premise of this class (see the note above): cleaning changes length,
    // so a raw offset cuts in the wrong place, but cleaning is monotonic, so a heading earlier
    // in the raw content is still earlier in the cleaned content. Order survives what position
    // does not. PageNumber cannot substitute, since two headings on one page are
    // indistinguishable by page.
    //
    // The sort is a re-assertion rather than a repair. GetHeadingsHelper builds the list by
    // walking DI's paragraphs forward once, and paragraph spans ascend through the document,
    // so this list already arrives ordered - measured at 1,273 headings across the big four
    // with zero out of order, zero ties and zero missing offsets. Sorting anyway costs one
    // pass over a few hundred items and removes the dependence on an upstream guarantee that
    // nothing states.
    //
    // A null offset means the paragraph carried no spans at all - explicitly not 0, since 0 is
    // a real offset and cannot double as "unknown" (DiGeometryHelpers.FirstOffset). Because
    // the input IS in reading order, the one thing known about such a heading is which
    // headings it came after, so it inherits the last offset seen and stays with its
    // neighbours. Sorting it last instead would move it to the one position in the document it
    // is guaranteed not to occupy, and its section boundary would go with it. It is counted
    // and returned either way: 0 of 1,273 means this is an extraction anomaly worth reporting,
    // not a routine input worth absorbing quietly.
    private static (List<Heading> Ordered, List<Heading> Offsetless) OrderByOffset(
        IReadOnlyList<Heading> headings)
    {
        var keyed      = new List<(Heading Heading, int Key, int Index)>(headings.Count);
        var offsetless = new List<Heading>();
        var carried    = 0;

        for (var i = 0; i < headings.Count; i++)
        {
            if (headings[i].Offset is { } offset) carried = offset;
            else offsetless.Add(headings[i]);

            keyed.Add((headings[i], carried, i));
        }

        // Index is the final tie-break, so a run of carried-offset headings keeps its arrival
        // order among themselves and stays behind the heading whose offset they borrowed.
        var ordered = keyed
            .OrderBy(k => k.Key)
            .ThenBy(k => k.Heading.PageNumber)
            .ThenBy(k => k.Index)
            .Select(k => k.Heading)
            .ToList();

        return (ordered, offsetless);
    }

    // Searches only within the heading's own page, and only at or after the previous
    // heading's position - so a heading whose text repeats (a running title, a term reused
    // as a heading later) matches the occurrence in document order rather than the first
    // one anywhere in the file.
    private static int FindInPage(
        string content, Heading heading, IReadOnlyList<PageSpan> pageSpans, int cursor)
    {
        var needle = Normalize(FirstLine(heading.Content));
        if (needle.Length == 0) return -1;

        var span = pageSpans.FirstOrDefault(s => s.PageNumber == heading.PageNumber);

        var from = span is null ? cursor : Math.Max(cursor, span.Offset);
        var to   = span is null ? content.Length : Math.Min(content.Length, span.Offset + span.Length);
        if (from >= to) return -1;

        var at = content.IndexOf(needle, from, to - from, StringComparison.Ordinal);
        if (at >= 0) return at;

        // Fall back to the whole document from the cursor. A heading whose page span is
        // slightly off (cleaning removed its page's content entirely, say) is still better
        // placed approximately than dropped.
        return content.IndexOf(needle, cursor, StringComparison.Ordinal);
    }

    // A merged heading ("Artikel 9\nBegrippen") carries both lines in Content but its Offset
    // covers only the first paragraph, so only the first line is reliably contiguous in the
    // text. GetHeadingsHelper's own comment predicted this exact consumer.
    private static string FirstLine(string content) =>
        content.Split('\n')[0].Trim();

    // Heading.Content is raw DI text; the page text has been through PdfCleaner. Applying
    // the same length-changing character transforms to the needle is what lets an exact
    // match work at all on a page that had ligatures, escaped punctuation or NBSPs.
    private static string Normalize(string s) =>
        s.Replace("ﬁ", "fi").Replace("ﬂ", "fl").Replace("ﬀ", "ff")
         .Replace("ﬃ", "ffi").Replace("ﬄ", "ffl")
         .Replace(' ', ' ')
         .Trim();

    // Turns located headings into contiguous sections, applying the two rules the cascade
    // was missing.
    private static (List<LocatedSection> Sections, int Merged) BuildSections(
        string content,
        List<(Heading Heading, int At, bool Found)> found,
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
            var (heading, at, _) = found[i];
            var end = i + 1 < found.Count ? found[i + 1].At : content.Length;

            // Rule 2 - paired zero-body headings. Hygienecode emits pairs like
            // "3.3 X: wat moet je doen..." immediately followed by "Acties als...", where
            // the first has no body between it and the second. Left alone each pair produces
            // a near-empty parent whose only content is its own heading line. The pair is
            // merged into one section: the second heading's text is folded into the first's,
            // and the section runs to wherever the second would have ended.
            var body = content[at..end].Trim();
            var headingLine = Normalize(FirstLine(heading.Content));

            if (body.Length <= headingLine.Length + 2 && i + 1 < found.Count)
            {
                var (next, _, _) = found[i + 1];
                var nextEnd = i + 2 < found.Count ? found[i + 2].At : content.Length;

                var mergedHeading = $"{FirstLine(heading.Content)} {FirstLine(next.Content)}".Trim();

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

            sections.Add(new LocatedSection(
                Index: index++,
                HeadingText: heading.Content.Trim(),
                HeadingPath: HeadingChainBuilder.Path(chains, heading.Offset, heading.Content),
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

    private static int PageAt(IReadOnlyList<PageSpan> spans, int offset)
    {
        foreach (var s in spans)
            if (offset >= s.Offset && offset < s.Offset + s.Length)
                return s.PageNumber;

        return spans.Count > 0 ? spans[0].PageNumber : 0;
    }
}
