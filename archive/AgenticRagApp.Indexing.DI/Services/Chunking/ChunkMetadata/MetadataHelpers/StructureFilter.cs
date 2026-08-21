using AgenticRagApp.Indexing.DI.Models;

namespace AgenticRagApp.Indexing.DI.Services;

// The document's extracted structure, narrowed to the pages one cut covers - so the cost
// scales with the CHUNK rather than with the document.
//
// Moved out of ChunkingService.ToChunk/OnPages unchanged, exclusions included:
//   - Lines is out on measured cost. It was 57% of the entire extraction payload by itself
//     (178 KB per document, a polygon per text line), and page-filtering only reduces it to
//     roughly one copy per chunk covering that page.
//   - Sections and Bookmarks are out for a stronger reason: they are per-DOCUMENT data, so
//     attaching them costs sections x chunk-count. That is what took the chunks blob to
//     772 MB for 3,046 chunks and OOM'd EmbedAndUploadActivity on 260812.
public static class StructureFilter
{
    public static ChunkStructure Build(PdfExtractionDocument doc, int pageStart, int pageEnd) =>
        new(Headings:       OnPages(doc.Headings,       h => h.PageNumber, pageStart, pageEnd),
            Boilerplate:    OnPages(doc.Boilerplate,    h => h.PageNumber, pageStart, pageEnd),
            Tables:         OnPages(doc.Tables,         t => t.PageNumber, pageStart, pageEnd),
            // The page the cut STARTS on. A cut spanning two differently-sized pages has no
            // single geometry, and the first page is the one a highlight would open on.
            Dimensions:     doc.PageSpans.FirstOrDefault(s => s.PageNumber == pageStart)?.Dimensions,
            SelectionMarks: OnPages(doc.SelectionMarks, s => s.PageNumber, pageStart, pageEnd),
            Figures:        OnPages(doc.Figures,        f => f.PageNumber, pageStart, pageEnd));

    // Sourced only from DI's own structured Figure.Caption - expect this empty on most current
    // documents. PdfCleaner separately extracts a figure's caption into the page text, which is
    // deliberately not threaded back into this structured field today.
    public static IReadOnlyList<string> CaptionsOf(ChunkStructure structure) =>
        structure.Figures
            .Where(f => !string.IsNullOrWhiteSpace(f.Caption))
            .Select(f => f.Caption!)
            .ToList();

    private static IReadOnlyList<T> OnPages<T>(
        IReadOnlyList<T> items, Func<T, int> pageOf, int start, int end) =>
        items.Where(i => pageOf(i) >= start && pageOf(i) <= end).ToList();
}
