using Azure.AI.ContentUnderstanding;
using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

// The document's outline from the typed response: Title and Headings from the Paragraphs CU
// itself classified (Role = Title | SectionHeading), heading DEPTH as the level CU rendered
// (the "#" count at the heading's own span), and the SectionInfo list itself.
//
// Ownership rule: this helper owns Title|SectionHeading; CuPageHelper owns the furniture roles.
internal static class CuOutlineHelper
{
    internal static (List<Heading> Headings, string? Title, List<SectionInfo> Sections) Build(
        DocumentContent document, string markdown, IReadOnlyList<PageSpan> pageSpans, List<string> warnings)
    {
        var paragraphs = (document.Paragraphs ?? Enumerable.Empty<DocumentParagraph>()).ToList();
        var sections   = (document.Sections   ?? Enumerable.Empty<DocumentSection>()).ToList();

        if (paragraphs.Count == 0)
            warnings.Add("Typed Paragraphs missing from the CU response; headings and title are empty.");

        var headings    = new List<Heading>();
        string? title   = null;
        var misaligned  = 0;
        var notAtMarker = 0;

        foreach (var paragraph in paragraphs)
        {
            // SemanticRole is an extensible enum (no constant patterns), hence == not switch.
            var role = paragraph.Role == SemanticRole.Title          ? "title"
                     : paragraph.Role == SemanticRole.SectionHeading ? "sectionHeading"
                     : null;
            if (role is null) continue;

            // Paragraph content VERBATIM (whitespace-trimmed only) - the documented shape:
            // paragraphs carry the text, and the markdown rendering is where the "#" markers
            // live ("paragraphs with a title or section heading roles are converted into
            // Markdown headings"). No defensive marker-trimming (user decision 2026-08-26,
            // "no heuristics anywhere") - the cu-raw-response capture is where the actual
            // content shape is verified.
            var text = (paragraph.Content ?? "").Trim();
            if (text.Length == 0) continue;

            var offset = paragraph.Span?.Offset;

            // Span check, on EVERY heading (2026-09-09 - it used to sample the first three). Two
            // things downstream relies on directly: the slice at the offset contains the heading's
            // own text (utf16 encoding, hardcoded by the SDK's typed Analyze overload), and the span
            // starts at the markdown marker - HeadingLocator cuts there instead of re-finding the
            // text, and Depth below is read there. Measured on the 260827 artifact: 2,451 of 2,451
            // headings start at "#". Counted, one aggregate warning each per document.
            var atMarker = false;
            if (offset is int o && paragraph.Span is { } span && o < markdown.Length)
            {
                var end   = Math.Min(o + Math.Max(span.Length, text.Length), markdown.Length);
                var probe = text.Length > 20 ? text[..20] : text;

                if (!markdown[o..end].Contains(probe, StringComparison.Ordinal)) misaligned++;
                else if (markdown[o] != '#')                                    notAtMarker++;
                else                                                             atMarker = true;
            }

            // DEPTH IS THE LEVEL CU RENDERED: the run of "#" at the heading's span. It used to be
            // derived from the section tree's nesting (root section = 0, one per "/sections/N"
            // hop); measured against the rendered markers on the 260827 artifact that derivation
            // disagreed on 1,189 of 2,451 headings - always by exactly one, and only in documents
            // that have a Title paragraph (557 agree / 1,186 disagree there; 666 / 3 without). The
            // service's own rendering is the fact; reading it at a typed offset is not "#-counting"
            // over the markdown, it is reading one attribute of an element whose position is typed.
            // Not capped at 6: the corpus carries four 7-hash headings (CAO GGZ, Hygienecode), and
            // 7 is what the service rendered. 0 = unknown, for a span that does not start at "#"
            // (warned above) - never a default level.
            var depth = atMarker ? MarkerRun(markdown, offset!.Value) : 0;

            headings.Add(new Heading(text, role, offset, CuPageHelper.PageAt(pageSpans, offset), depth));

            // The title is the Role=Title paragraph and nothing else. A fallback to the first
            // section heading sat here until 2026-09-09; measured on the 260827 artifact it fired
            // on 12 of 51 documents and produced "Inleiding", "INLEIDING", "Inhoudsopgave" and a
            // copyright line as document titles - a plausible substitute is not the value
            // (docs/2609/260909/cu-helpers-review.md). Null is the honest answer, and
            // MissingTitleCount now measures exactly how often the service reports no title.
            if (role == "title") title ??= text;
        }

        if (misaligned > 0)
            warnings.Add(
                $"Typed span misalignment: {misaligned} of {headings.Count} heading(s) not found at their reported offset - " +
                "check AnalysisResult.StringEncoding.");

        if (notAtMarker > 0)
            warnings.Add(
                $"{notAtMarker} of {headings.Count} heading span(s) do not start at a markdown '#' marker; " +
                "their Depth is 0 and the section boundary HeadingLocator opens there will leave the marker with the previous section.");

        return (headings, title, BuildSections(sections));
    }

    private static int MarkerRun(string markdown, int offset)
    {
        var n = 0;
        while (offset + n < markdown.Length && markdown[offset + n] == '#') n++;
        return n;
    }

    // SectionInfo: the service's span and its raw element refs, verbatim. A per-ref resolved
    // label ("table 3x4", the first 80 chars of a paragraph) was built here until 2026-09-09;
    // nothing read it. The tree is still what HeadingChainBuilder walks for ancestor chains.
    private static List<SectionInfo> BuildSections(IReadOnlyList<DocumentSection> sections) =>
        [.. sections.Select(s => new SectionInfo(
            Spans:    s.Span is { } span ? [new SectionSpan(span.Offset, span.Length)] : [],
            Elements: [.. s.Elements ?? Enumerable.Empty<string>()]))];
}
