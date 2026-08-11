using System.Text.RegularExpressions;
using Azure.AI.DocumentIntelligence;
using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Dereferences a DocumentSection's raw JSON-pointer Elements ("/paragraphs/15",
// "/tables/2", "/figures/0", "/sections/3") against the same AnalyzeResult they were
// produced from, turning "an index into some collection" into "what's actually there" -
// pre-chunking-action-items.md A1: DI's section tree is carried but never dereferenced
// today. DI's own pointer shape is always exactly two segments, a collection name and a
// numeric index - no deeper path to walk.
internal static partial class ResolveSectionElementsHelper
{
    [GeneratedRegex(@"^/(paragraphs|tables|figures|sections)/(\d+)$")]
    private static partial Regex ElementPointer();

    public static IReadOnlyList<SectionElementRef> Resolve(IReadOnlyList<string> elements, AnalyzeResult result)
    {
        var resolved = new List<SectionElementRef>(elements.Count);

        foreach (var pointer in elements)
        {
            // TryParse, not Parse: "\d+" is unbounded, so a pointer whose index doesn't fit
            // an int ("/paragraphs/99999999999") matches the shape and would then throw
            // OverflowException straight past every typed error path - the same crash this
            // helper's range guard exists to prevent. An index that large is not a usable
            // reference either way, so it falls into the unrecognized-shape branch.
            var match = ElementPointer().Match(pointer);
            if (!match.Success || !int.TryParse(match.Groups[2].Value, out var index))
            {
                resolved.Add(new SectionElementRef(pointer, -1, null));
                continue;
            }

            var kind = match.Groups[1].Value;

            resolved.Add(new SectionElementRef(kind, index, ResolveText(kind, index, result)));
        }

        return resolved;
    }

    private static string? ResolveText(string kind, int index, AnalyzeResult result) => kind switch
    {
        "paragraphs" => Within(result.Paragraphs, index, p => Summarize(p.Content)),
        "tables"     => Within(result.Tables, index, t => $"table {t.RowCount}x{t.ColumnCount}"),

        // Falls back to "figure N" rather than null when a figure has neither caption nor
        // Id: null on a resolved element would be indistinguishable from the out-of-range
        // case below, which is the only thing null is supposed to mean here. Same reason
        // tables always yield a summary instead of their (absent) text.
        "figures"    => Within(result.Figures, index, f => f.Caption?.Content ?? f.Id ?? $"figure {index}"),

        // A nested section's own Elements aren't walked here - GetSectionsHelper resolves
        // every top-level DI section independently via this same method, so recursing here
        // would either re-walk the same paragraphs a second time (if the nested section is
        // also a top-level entry in result.Sections) or risk a cycle DI itself doesn't
        // document as impossible. Left as a bare reference to the subsection's index.
        "sections"   => Within(result.Sections, index, _ => $"section {index}"),

        _ => null,
    };

    // Text is a scannable label for what a pointer points at, not a second copy of the
    // document. SectionInfo is carried onto every chunk (ChunkingService) and serialized
    // into the chunks.json hand-off and the Stage 2 archive (see DocumentChunk's note on
    // why these fields are deliberately not [JsonIgnore]'d), so an untruncated paragraph
    // here would repeat the document's entire prose once per chunk in every one of those
    // payloads. A consumer that needs the real paragraph follows Kind + Index.
    private const int MaxTextLength = 120;

    private static string Summarize(string content)
    {
        if (content.Length <= MaxTextLength) return content;

        // Never cut between a surrogate pair: a lone surrogate is not valid UTF-16 and
        // would only be discovered downstream, at serialization.
        var cut = char.IsHighSurrogate(content[MaxTextLength - 1]) ? MaxTextLength - 1 : MaxTextLength;

        return string.Concat(content.AsSpan(0, cut), "…");
    }

    // Out-of-range guarded the same way GetPagesHelper.SliceBySpans clamps rather than
    // throws - a resolution helper must never crash the pipeline over an index DI's own
    // section pointer and DI's own array happened to disagree on. (The index itself can't
    // be negative - the pointer regex has no sign - but the check costs nothing and keeps
    // this usable as a general bounds guard.)
    private static string? Within<T>(IReadOnlyList<T>? items, int index, Func<T, string?> select) =>
        items is { } list && index >= 0 && index < list.Count ? select(list[index]) : null;
}
