using Azure.AI.DocumentIntelligence;
using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// DI's own sections: the closest prebuilt-layout gets to semantic chunk boundaries,
// vs the page-only boundaries GetPages relies on today.
// - Every span kept (not anchor-only like the others): a section only means something
//   as a start-to-end range.
// - Elements stay as raw JSON-pointer strings; resolving them is a future
//   chunk-builder's job.
internal static class GetSectionsHelper
{
    public static IReadOnlyList<SectionInfo> GetSections(AnalyzeResult result) =>
        (result.Sections ?? [])
            .Select(s => new SectionInfo(
                (s.Spans ?? []).Select(sp => new SectionSpan(sp.Offset, sp.Length)).ToList(),
                s.Elements?.ToList() ?? []))
            .ToList();
}
