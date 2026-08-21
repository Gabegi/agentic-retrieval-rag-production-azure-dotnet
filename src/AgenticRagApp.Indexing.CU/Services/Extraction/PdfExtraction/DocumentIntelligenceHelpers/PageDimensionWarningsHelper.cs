using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

// Compares Document Intelligence's reported per-page Width/Height/Unit
// (GetPageDimensionsHelper) against the PDF's own native page size, read via PdfPig
// (PdfNativeMetadataExtractor.TryGetPageDimensions, DocMetadata.NativePageDimensions).
// A mismatch is a data-quality signal, not a truncated/broken document - unlike the
// page *count* check in PDFDocumentIntelligenceAnalyzer.BuildResults, this only ever
// warns, never fails.
internal static class PageDimensionWarningsHelper
{
    // Width/Height agreement tolerance, in inches. DI's own values aren't an exact
    // echo of the native MediaBox (floating-point/measurement rounding), so an exact
    // equality check would false-positive on every document.
    private const double ToleranceInches = 0.05;

    private const double PointsPerInch = 72.0;

    // internal (not private): testable without a live DI call or a real PDF, same as
    // GetQualityWarningsHelper's methods.
    public static (IReadOnlyList<AnalysisWarning> Warnings, IReadOnlyList<AnalysisWarning> Infos) GetPageDimensionWarnings(
        IReadOnlyList<PageDimensions>? nativeDimensions, IReadOnlyList<PageDimensions> diDimensions, string blobName)
    {
        // Native read itself failed (TryGetPageDimensions already warned about it via
        // PdfNativeMetadataExtractor's own diagnostics) - nothing to compare against.
        if (nativeDimensions is null) return ([], []);

        var warnings = new List<AnalysisWarning>();
        var infos    = new List<AnalysisWarning>();

        var nativeByPage = nativeDimensions.ToDictionary(d => d.PageNumber);

        foreach (var di in diDimensions)
        {
            if (!nativeByPage.TryGetValue(di.PageNumber, out var native))
                continue;

            // Only "inch" is a unit this corpus has ever produced (see
            // docs/2607/260731/page-dimensions-comparison-todo.md); a different unit
            // isn't wrong, just not something the conversion below knows how to handle
            // yet, so that page is skipped rather than compared against a wrong assumption.
            if (!string.Equals(di.Unit, "inch", StringComparison.OrdinalIgnoreCase))
            {
                infos.Add(new AnalysisWarning(
                    "PageDimensionUnitUnsupported",
                    $"Page {di.PageNumber}: Document Intelligence reported unit '{di.Unit}', not 'inch'; native/DI page size comparison skipped.",
                    blobName));
                continue;
            }

            if (di.Width is not { } diWidthIn || di.Height is not { } diHeightIn ||
                native.Width is not { } nativeWidthPt || native.Height is not { } nativeHeightPt)
                continue;

            var nativeWidthIn  = nativeWidthPt  / PointsPerInch;
            var nativeHeightIn = nativeHeightPt / PointsPerInch;

            var widthDiff  = Math.Abs(diWidthIn  - nativeWidthIn);
            var heightDiff = Math.Abs(diHeightIn - nativeHeightIn);

            if (widthDiff > ToleranceInches || heightDiff > ToleranceInches)
                warnings.Add(new AnalysisWarning(
                    "PageDimensionMismatch",
                    $"Page {di.PageNumber}: Document Intelligence reported {diWidthIn:F3}in x {diHeightIn:F3}in, " +
                    $"native PDF page is {nativeWidthIn:F3}in x {nativeHeightIn:F3}in (tolerance {ToleranceInches}in).",
                    blobName));
        }

        return (warnings, infos);
    }
}
