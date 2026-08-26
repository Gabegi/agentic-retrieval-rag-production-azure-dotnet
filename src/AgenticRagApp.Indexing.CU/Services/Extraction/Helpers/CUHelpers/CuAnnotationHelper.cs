using Azure.AI.ContentUnderstanding;
using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

// Annotations from the typed response (DocumentContent.Annotations, enableLayout): PDF
// highlights, underlines and comment threads. annotationFormat=markdown is fixed on
// prebuilt-documentSearch, so the markdown may also carry ==highlight==-style syntax - this
// helper reads the typed side only, which is the complete record (author, comment thread,
// tags) the markdown syntax cannot express.
internal static class CuAnnotationHelper
{
    internal static List<AnnotationInfo> Build(DocumentContent document, IReadOnlyList<PageSpan> pageSpans) =>
        [.. (document.Annotations ?? Enumerable.Empty<DocumentAnnotation>())
            .Select(a =>
            {
                var offset = a.Spans is { Count: > 0 } spans ? spans.Min(s => s.Offset) : (int?)null;
                return new AnnotationInfo(
                    Id:         a.Id,
                    Kind:       a.Kind.ToString(),
                    Author:     a.Author,
                    Comments:   [.. (a.Comments ?? Enumerable.Empty<DocumentAnnotationComment>())
                                    .Where(c => !string.IsNullOrWhiteSpace(c.Message))
                                    .Select(c => string.IsNullOrWhiteSpace(c.Author)
                                        ? c.Message! : $"{c.Author}: {c.Message}")],
                    Offset:     offset,
                    PageNumber: CuPageHelper.PageAt(pageSpans, offset),
                    Tags:       [.. a.Tags ?? Enumerable.Empty<string>()]);
            })];
}
