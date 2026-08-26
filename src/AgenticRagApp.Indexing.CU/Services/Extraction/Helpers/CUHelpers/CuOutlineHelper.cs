using Azure.AI.ContentUnderstanding;
using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

// The document's outline from the typed response: Title and Headings from the Paragraphs CU
// itself classified (Role = Title | SectionHeading - no "#"-counting), heading DEPTH from the
// section tree's nesting (DocumentSection.Elements), and the SectionInfo list itself.
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

        // Paragraph index -> depth, from the section tree. Empty when there is no tree - every
        // heading then defaults to depth 1, flattening DeclaredBoundaryStrategy's hierarchy but
        // never dropping a boundary.
        var depthByParagraph = DepthMap(sections);
        if (sections.Count == 0 && paragraphs.Any(p => p.Role == SemanticRole.SectionHeading))
            warnings.Add("Typed Sections missing from the CU response; heading depths default to 1.");

        var headings   = new List<Heading>();
        string? title  = null;
        var spotChecks = 0;

        for (var i = 0; i < paragraphs.Count; i++)
        {
            var paragraph = paragraphs[i];
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
            var depth  = role == "title"
                ? 1
                : Math.Clamp(depthByParagraph.GetValueOrDefault(i, 1), 1, 6);

            headings.Add(new Heading(text, role, offset, CuPageHelper.PageAt(pageSpans, offset), depth));

            title ??= role == "title" ? text : null;

            // Span sanity: the typed offset should land on this heading's own text in the
            // markdown (utf16 encoding, hardcoded by the SDK's typed Analyze overload). Checked on
            // the first few headings only; a drift here is one warning, not N.
            if (spotChecks < 3 && offset is int o && paragraph.Span is { } span)
            {
                spotChecks++;
                var end   = Math.Min(o + Math.Max(span.Length, text.Length), markdown.Length);
                var slice = o <= markdown.Length ? markdown[Math.Min(o, markdown.Length)..end] : "";
                var probe = text.Length > 20 ? text[..20] : text;
                if (!slice.Contains(probe, StringComparison.Ordinal))
                    warnings.Add(
                        $"Typed span misalignment: heading '{Truncate(text)}' not found at its reported offset {o} - " +
                        "check AnalysisResult.StringEncoding.");
            }
        }

        // Title fallback: no Role=Title paragraph -> the first section heading, same order of
        // preference the markdown mapper used (first H1, else first heading).
        title ??= headings.FirstOrDefault()?.Content;

        return (headings, title, BuildSections(document, sections, paragraphs));
    }

    // Depth by nesting in the section tree: an UNREFERENCED section is a root at depth 0, a
    // "/sections/N" child sits one deeper, and a "/paragraphs/N" ref gives paragraph N its
    // section's depth. Root-level headings therefore land at depth 0 -> clamped to 1 by the
    // caller, and each nesting level below adds one - which is the H1/H2/... shape
    // DeclaredBoundaryStrategy routes on.
    private static Dictionary<int, int> DepthMap(IReadOnlyList<DocumentSection> sections)
    {
        var result = new Dictionary<int, int>();
        if (sections.Count == 0) return result;

        var referenced = new HashSet<int>(
            sections.SelectMany(s => s.Elements ?? Enumerable.Empty<string>())
                .Select(e => RefIndex(e, "sections"))
                .Where(i => i >= 0));

        var visited = new HashSet<int>();
        var queue   = new Queue<(int Index, int Depth)>();

        for (var i = 0; i < sections.Count; i++)
            if (!referenced.Contains(i))
                queue.Enqueue((i, 0));

        // Every section referenced but never reached (a cycle, which the service should never
        // produce) simply contributes no depths - map what's there.
        while (queue.Count > 0)
        {
            var (index, depth) = queue.Dequeue();
            if (index < 0 || index >= sections.Count || !visited.Add(index)) continue;

            foreach (var element in sections[index].Elements ?? Enumerable.Empty<string>())
            {
                var child = RefIndex(element, "sections");
                if (child >= 0) { queue.Enqueue((child, depth + 1)); continue; }

                var paragraph = RefIndex(element, "paragraphs");
                if (paragraph >= 0) result.TryAdd(paragraph, depth);
            }
        }

        return result;
    }

    // SectionInfo: the raw refs verbatim plus a resolved, human-scannable label per ref - the
    // same record shape and label conventions the DI-era resolver produced, so downstream
    // consumers and the reports read on unchanged.
    private static List<SectionInfo> BuildSections(
        DocumentContent document, IReadOnlyList<DocumentSection> sections, IReadOnlyList<DocumentParagraph> paragraphs)
    {
        var tables  = (document.Tables  ?? Enumerable.Empty<DocumentTable>()).ToList();
        var figures = (document.Figures ?? Enumerable.Empty<DocumentFigure>()).ToList();

        return [.. sections.Select(s =>
        {
            var elements = (s.Elements ?? Enumerable.Empty<string>()).ToList();
            return new SectionInfo(
                Spans:            s.Span is { } span ? [new SectionSpan(span.Offset, span.Length)] : [],
                Elements:         elements,
                ResolvedElements: [.. elements.Select(e => Resolve(e, paragraphs, tables, figures))]);
        })];
    }

    private static SectionElementRef Resolve(
        string pointer,
        IReadOnlyList<DocumentParagraph> paragraphs,
        IReadOnlyList<DocumentTable> tables,
        IReadOnlyList<DocumentFigure> figures)
    {
        foreach (var kind in (string[])["paragraphs", "tables", "figures", "sections"])
        {
            var index = RefIndex(pointer, kind);
            if (index < 0) continue;

            var text = kind switch
            {
                "paragraphs" => index < paragraphs.Count ? Truncate(paragraphs[index].Content ?? "") : null,
                "tables"     => index < tables.Count ? $"table {tables[index].RowCount}x{tables[index].ColumnCount}" : null,
                "figures"    => index < figures.Count ? figures[index].Caption?.Content ?? figures[index].Id : null,
                _            => $"section {index}",
            };
            return new SectionElementRef(kind, index, text);
        }

        // Unrecognized pointer shape: carried verbatim rather than silently dropped.
        return new SectionElementRef(pointer, -1, null);
    }

    private static int RefIndex(string pointer, string collection) =>
        pointer.StartsWith($"/{collection}/", StringComparison.Ordinal) &&
        int.TryParse(pointer.AsSpan(collection.Length + 2), out var index) && index >= 0
            ? index : -1;

    private static string Truncate(string text) =>
        text.Length <= 80 ? text : text[..80] + "…";
}
