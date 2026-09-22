using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

// The document's extracted structure, narrowed to the pages one cut covers - so the cost
// scales with the CHUNK rather than with the document.
//
// Moved out of ChunkingService.ToChunk/OnPages unchanged, exclusions included:
//   - Lines is out on measured cost. It was 57% of the entire extraction payload by itself
//     (178 KB per document, a polygon per text line), and page-filtering only reduces it to
//     roughly one copy per chunk covering that page.
//   - Sections are out for a stronger reason: they are per-DOCUMENT data, so
//     attaching them costs sections x chunk-count. That is what took the chunks blob to
//     772 MB for 3,046 chunks and OOM'd EmbedAndUploadActivity on 260812.
public static class StructureFilter
{
    public static ChunkStructure Build(PdfExtractionDocument doc, int pageStart, int pageEnd) =>
        new(Headings:       OnPages(doc.Headings,       h => h.PageNumber, pageStart, pageEnd),
            Boilerplate:    OnPages(doc.Boilerplate,    h => h.PageNumber, pageStart, pageEnd),
            // Tables are the one element filtered on a RANGE rather than an anchor page
            // (2026-09-08): a table spanning pages 12-14 has an anchor page of 12, so an
            // anchor test attached it to chunks on page 12 only and a continuation fragment on
            // page 14 reported table_count 0 while carrying half the table's rows. The range
            // comes from the parsed regions; without geometry TableInfo falls back to the
            // anchor, which is exactly the old behaviour.
            //
            // Attached WITHOUT its cells (2026-09-22): the range test puts one table on every
            // chunk of every page it touches, so 3,619 tables became 21,921 entries on run
            // 260921/1 and their Cells arrays were 122.7 MB of the 282 MB chunking artifact
            // (43.5%). Nothing on the chunk side reads a cell - table_count is the entry count,
            // has_table reads doc.Tables, the merged-cell report reads the extraction document -
            // and the cells stay in full on the extraction artifact. Same rule that took Lines
            // and Sections out of this record: a per-document payload multiplied by chunk count.
            Tables:         Overlapping(doc.Tables,     t => t.OverlapsPages(pageStart, pageEnd))
                                .Select(t => t with { Cells = [] })
                                .ToList(),
            // The page the cut STARTS on. A cut spanning two differently-sized pages has no
            // single geometry, and the first page is the one a highlight would open on.
            Dimensions:     doc.PageSpans.FirstOrDefault(s => s.PageNumber == pageStart)?.Dimensions,
            Figures:        OnPages(doc.Figures,        f => f.PageNumber, pageStart, pageEnd),
            Annotations:    OnPages(doc.Annotations,    a => a.PageNumber, pageStart, pageEnd),
            Hyperlinks:     OnPages(doc.Hyperlinks,     h => h.PageNumber, pageStart, pageEnd));

    // Sourced from CU's structured Figure.Caption. The generated Description is deliberately
    // NOT folded in here - it already rides inline in the markdown (and so in chunk content).
    public static IReadOnlyList<string> CaptionsOf(ChunkStructure structure) =>
        structure.Figures
            .Where(f => !string.IsNullOrWhiteSpace(f.Caption))
            .Select(f => f.Caption!)
            .ToList();

    // The two index-field projections (decision 2026-08-26: annotations and hyperlinks go into
    // chunk metadata AND index fields). Hyperlink rows prefer the target (the searchable,
    // clickable fact); a link with display text but no target still contributes its text.
    public static IReadOnlyList<string> HyperlinksOf(ChunkStructure structure) =>
        (structure.Hyperlinks ?? [])
            .Select(h => h.Uri ?? h.Content)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    // One string per annotation: the comment thread when there is one (that is the content a
    // query could match), else kind+author as the bare fact a markup existed.
    public static IReadOnlyList<string> AnnotationsOf(ChunkStructure structure) =>
        (structure.Annotations ?? [])
            .Select(a => a.Comments.Count > 0
                ? string.Join(" | ", a.Comments)
                : string.IsNullOrWhiteSpace(a.Author) ? a.Kind : $"{a.Kind} ({a.Author})")
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();

    private static IReadOnlyList<T> OnPages<T>(
        IReadOnlyList<T> items, Func<T, int> pageOf, int start, int end) =>
        items.Where(i => pageOf(i) >= start && pageOf(i) <= end).ToList();

    // For elements that occupy a page RANGE rather than sitting on one page. The predicate is
    // the element's own, so the definition of "which pages is this on" stays with the type that
    // knows - see TableInfo.OverlapsPages.
    private static IReadOnlyList<T> Overlapping<T>(IReadOnlyList<T> items, Func<T, bool> overlaps) =>
        items.Where(overlaps).ToList();
}
