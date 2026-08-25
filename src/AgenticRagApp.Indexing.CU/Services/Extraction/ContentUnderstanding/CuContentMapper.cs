using Azure.AI.ContentUnderstanding;
using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

// One validated Content Understanding response -> everything one document contributes to the
// index. The single place the service's model is translated into this pipeline's, so the
// coordinate rules (CuMarkdownPager for cleaned Content, CuStructureMapper for raw structural
// offsets) are decided once rather than per caller.
internal static class CuContentMapper
{
    // Content Understanding does not detect document language. Verified against the SDK surface
    // (Azure.AI.ContentUnderstanding 1.1.0): the only locale-bearing members are
    // ContentAnalyzerConfig.Locales, which is an input hint, and TranscriptPhrase.Locale, which
    // is audio/video. Document Intelligence returned AnalyzeResult.Languages per span and
    // LanguageDetectionHelper picked the dominant one; there is no counterpart.
    //
    // "nl" is the corpus: Dutch care-organisation policy documents, plus a small number of
    // English ones that are now knowingly mislabelled. It is also exactly what
    // LanguageDetectionHelper already fell back to when DI returned nothing. The eventual home
    // for real detection is the analyzer's own fieldSchema (one model-extracted field per
    // document); a stopword heuristic here would be a third answer to a question the analyzer
    // can answer properly.
    private const string DefaultLanguage = "nl";

    // Everything mapped out of one response, before per-file assembly.
    internal sealed record MappedDocument(
        string                    Content,
        IReadOnlyList<PageSpan>   PageSpans,
        PdfDocumentStructure      Structure,
        string                    Title,
        DocumentProfile           Profile,
        string                    Language);

    public static MappedDocument Map(DocumentContent document, string blobName, long fileSizeBytes)
    {
        var dimensions = MapPageDimensions(document);

        // Structure first: the picture-only join below needs figures, and the profile needs
        // headings and boilerplate.
        var structure = CuStructureMapper.Map(document, dimensions);

        // A page with at least one figure and no words of its own. This is the only way a mixed
        // document (38 normal pages, 2 diagram pages) can be spotted - a document-level density
        // gate passes such a file comfortably. Computed from the response rather than after
        // cleaning so an empty page that never had words is distinguished from one whose text
        // was stripped.
        var pictureOnlyByPage = BuildPictureOnlyMap(document, structure.Figures);

        var (content, pageSpans) = CuMarkdownPager.BuildContent(document, dimensions, pictureOnlyByPage);

        // Per-page text as it ended up in Content, which is what the profile must measure - the
        // routing gate compares chars and estimated tokens against thresholds calibrated on
        // cleaned text.
        var pageTexts = pageSpans
            .Select(s => content.Substring(s.Offset, s.Length))
            .ToList();

        var profile = DocumentProfileHelper.Compute(
            pageTexts,
            structure.Figures,
            fileSizeBytes,
            structure.Headings,
            structure.Boilerplate,
            structure.SelectionMarks,
            // Every Heading.Offset addresses CU's RAW markdown, so that is the only end bracket
            // the max-section-gap measurement can use - see DocumentProfileHelper.MaxGap.
            rawContentLength: (document.Markdown ?? "").Length);

        return new MappedDocument(
            content, pageSpans, structure,
            TitleFallback.GetTitle(blobName),
            profile,
            DefaultLanguage);
    }

    private static List<PageDimensions> MapPageDimensions(DocumentContent document)
    {
        // Unit is document-level in CU (it was per-page in Document Intelligence), so every
        // page carries the same one. "inch" matches what DI reported for PDFs, which is what
        // any stored dimension is currently compared against.
        var unit = document.Unit?.ToString() ?? "inch";

        return [.. (document.Pages ?? []).Select(p => new PageDimensions(p.PageNumber, p.Width, p.Height, unit))];
    }

    private static Dictionary<int, bool> BuildPictureOnlyMap(
        DocumentContent document, IReadOnlyList<FigureInfo> figures)
    {
        var figurePages = figures.Select(f => f.PageNumber).ToHashSet();

        return (document.Pages ?? [])
            .ToDictionary(
                p => p.PageNumber,
                p => figurePages.Contains(p.PageNumber) && (p.Words is null || p.Words.Count == 0));
    }
}
