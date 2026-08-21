using Azure.AI.DocumentIntelligence;
using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

// DI's own sections: the closest prebuilt-layout gets to semantic chunk boundaries,
// vs the page-only boundaries GetPages relies on today.
// - Every span kept (not anchor-only like the others): a section only means something
//   as a start-to-end range.
// - Elements stay as raw JSON-pointer strings, kept verbatim; ResolvedElements is the
//   same list dereferenced against this same result (ResolveSectionElementsHelper).
internal static class GetSectionsHelper
{
    public static IReadOnlyList<SectionInfo> GetSections(AnalyzeResult result) =>
        (result.Sections ?? [])
            .Select(s =>
            {
                var elements = s.Elements?.ToList() ?? [];
                return new SectionInfo(
                    (s.Spans ?? []).Select(sp => new SectionSpan(sp.Offset, sp.Length)).ToList(),
                    elements,
                    ResolveSectionElementsHelper.Resolve(elements, result));
            })
            .ToList();
}
