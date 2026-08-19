using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Indexing.Pdf.Services;

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
                // The match lands on the heading TEXT, but DI renders headings as markdown -
                // "### Kop" - so the boundary cut there leaves the "### " tail ending the
                // PREVIOUS section's body: 1,754 of 2,997 chunks in the 260818 index ended in
                // a bare marker line. Pull the boundary back over the marker so it stays with
                // the heading it belongs to, and both sections remain pure slices.
                at = IncludeMarkdownMarker(content, at);

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

        var (built, merged) = BuildSections(content, found, pageSpans, chains);

        // Table captions DI did not call headings become boundaries of their own - otherwise a
        // section spanning nine salary tables stamps all nine with one heading, and a chunk
        // ends up labelled with a functiegroep it does not contain. See TableCaptionSplitter.
        var sections = TableCaptionSplitter.Split(content, built);

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
    // the same character transforms to the needle is what lets an exact match work at all
    // on a page that had ligatures, NBSPs, decomposed diacritics or folded symbols - the
    // shared repair (ExtractedTextRepair) IS PdfCleaner's character set, so needle and
    // haystack cannot drift apart again.
    private static string Normalize(string s) =>
        Services.ExtractedTextRepair.Repair(s).Trim();

    // Walks a located heading's position back over the markdown marker that precedes it -
    // "#{1,6}" plus spacing - but only when that marker starts its own line, so a '#' inside
    // running text is never absorbed. Returns the original position when there is no marker.
    private static int IncludeMarkdownMarker(string content, int at)
    {
        var i = at;
        while (i > 0 && content[i - 1] is ' ' or '\t') i--;

        var hashes = 0;
        while (i > 0 && content[i - 1] == '#' && hashes < 6) { i--; hashes++; }

        if (hashes == 0) return at;

        return i == 0 || content[i - 1] == '\n' ? i : at;
    }

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
            //
            // This is the SECOND merge of the same phenomenon, and the two have to agree.
            // GetHeadingsHelper already merges adjacent heading-role paragraphs at extraction,
            // and it deliberately refuses to when the first is a bare numbered label: two
            // consecutive "Artikel 8" / "Artikel 9" markers are separate short articles, not a
            // pair (BareLabelFollowedByAnotherHeading_NeitherMerges). This rule had no such
            // gate, so it re-merged exactly the pairs extraction had just decided to keep apart
            // - overriding a deliberate decision from the one place that can see paragraph
            // adjacency, and inflating a counter that shares its name with extraction's.
            //
            // Gated on the same regex rather than a copy of it, for the same reason
            // GetQualityWarningsHelper shares it: two patterns that must agree, kept as one.
            // The boundary now includes the heading's markdown marker (IncludeMarkdownMarker),
            // so strip it before the zero-body length comparison below - otherwise a marker'd
            // pair reads as having "### " of body and the merge stops firing.
            var body = content[at..end].Trim().TrimStart('#').TrimStart();
            var headingLine = Normalize(FirstLine(heading.Content));

            var isBareLabel = GetHeadingsHelper.BareNumberedLabelWithWord()
                                               .IsMatch(FirstLine(heading.Content));

            // The same refusal, made on shape instead of on bareness. A pair is a heading and
            // its continuation; two headings at the same structural level are separate
            // sections, and folding them yields one segment naming both articles. See
            // HeadingChainBuilder.AreSameStructuralLevel. The vacant articles this stops
            // merging become heading-only sections, which is what they are - the residue
            // filter, not this merge, is the right place to drop them.
            var nextIsSibling = i + 1 < found.Count
                && HeadingChainBuilder.AreSameStructuralLevel(
                       HeadingTextNormalizer.Flatten(heading.Content) ?? "",
                       HeadingTextNormalizer.Flatten(found[i + 1].Heading.Content) ?? "");

            if (!isBareLabel && !nextIsSibling && body.Length <= headingLine.Length + 2 && i + 1 < found.Count)
            {
                var (next, _, _) = found[i + 1];
                var nextEnd = i + 2 < found.Count ? found[i + 2].At : content.Length;

                // Every line of both headings, space-joined - see HeadingTextNormalizer. Taking
                // the first line of each dropped the title half of an already-merged heading
                // ("Artikel 9\nBegrippen" became "Artikel 9"), which is the half a query matches.
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

            // Same one shape as the merged branch above: every line, space-joined. This stored
            // heading.Content.Trim() whole, so an extraction-merged heading reached heading_text,
            // heading_path and the embedded prefix with its newline intact.
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

    private static int PageAt(IReadOnlyList<PageSpan> spans, int offset)
    {
        foreach (var s in spans)
            if (offset >= s.Offset && offset < s.Offset + s.Length)
                return s.PageNumber;

        return spans.Count > 0 ? spans[0].PageNumber : 0;
    }
}
