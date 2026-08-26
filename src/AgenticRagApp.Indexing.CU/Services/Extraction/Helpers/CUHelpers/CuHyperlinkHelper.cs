using Azure.AI.ContentUnderstanding;
using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

// Hyperlinks from the typed response (DocumentContent.Hyperlinks, enableLayout): display text
// plus target for every link preserved from the document's digital content - 483 in the
// corpus. The markdown carries them inline as [text](url) too; the typed list is what chunk
// metadata and the index field are built from (decision 2026-08-26).
internal static class CuHyperlinkHelper
{
    internal static List<HyperlinkInfo> Build(DocumentContent document, IReadOnlyList<PageSpan> pageSpans) =>
        [.. (document.Hyperlinks ?? Enumerable.Empty<DocumentHyperlink>())
            .Where(h => !string.IsNullOrWhiteSpace(h.Uri) || !string.IsNullOrWhiteSpace(h.Content))
            .Select(h =>
            {
                var offset = h.Span?.Offset;
                return new HyperlinkInfo(h.Content, h.Uri, offset, CuPageHelper.PageAt(pageSpans, offset));
            })];
}
