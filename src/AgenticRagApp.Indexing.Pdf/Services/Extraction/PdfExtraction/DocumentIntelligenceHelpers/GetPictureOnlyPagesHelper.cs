using Azure.AI.DocumentIntelligence;
using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// C5 (docs/2608/260811/pre-chunking-action-items.md) - "join FigureInfo.PageNumber against
// ZeroWordsOnPage/EmptyPageContent". Document-level Picture routing (DocumentProfileHelper)
// can't catch a mixed document (38 normal pages + 2 diagram pages) - those 2 pages currently
// produce vector-residue chunks or nothing. This flags the individual pages responsible,
// reusing the exact same predicates GetQualityWarningsHelper.GetZeroWordWarnings and
// GetPagesHelper's EmptyPageContent warning already compute (not re-derived from scratch, not
// parsed back out of the warning strings) - joined against FigureInfo.PageNumber so a genuinely
// blank page (zero words, no figures) is never mistaken for a picture-only one.
internal static class GetPictureOnlyPagesHelper
{
    public static IReadOnlyList<PdfPageRecord> MarkPictureOnlyPages(
        AnalyzeResult analysis, IReadOnlyList<PdfPageRecord> pages, IReadOnlyList<FigureInfo> figures)
    {
        var pagesWithFigures = figures.Select(f => f.PageNumber).ToHashSet();

        // Same "DI reported no words at all" test as GetZeroWordWarnings, read directly off
        // DI's own page objects rather than the AnalysisWarning it produces from them.
        var zeroWordPages = analysis.Pages
            .Where(p => (p.Words?.Count ?? 0) == 0)
            .Select(p => p.PageNumber)
            .ToHashSet();

        return pages
            .Select(p => p with
            {
                // Same "content.Length == 0" test GetPagesHelper's EmptyPageContent warning
                // already uses, applied to the cleaned PageContent this record already carries.
                IsPictureOnlyPage = pagesWithFigures.Contains(p.PageNumber)
                    && (zeroWordPages.Contains(p.PageNumber) || p.PageContent.Length == 0),
            })
            .ToList();
    }
}
