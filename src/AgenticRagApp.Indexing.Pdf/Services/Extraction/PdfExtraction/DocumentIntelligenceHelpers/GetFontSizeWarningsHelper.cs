using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// A3 (docs/2608/260811/pre-chunking-action-items.md) - a font-size proxy derived from
// LineInfo.Polygon, independent of DI's own Role classification. GetHeadingsHelper only
// ever trusts paragraphs DI itself tagged Title/SectionHeading (see that file's comment on
// GetHeadings) - this is the "classic heading signal" that was missing alongside it: line
// height relative to a per-document body-text baseline. Two directions, both warnings only,
// never mutating the heading list itself:
//   - a heading-role line that isn't meaningfully taller than the document's own body-text
//     baseline - an over-firing candidate (the motivating Buddy case in pre-chunking-
//     action-items.md)
//   - a body line meaningfully taller than baseline that ISN'T part of any detected heading -
//     a possible heading DI's own Role classifier missed (the "Checklist" label miss found
//     on a Small document - see docs/2608/260811/d1-small-heading-quality-findings.md)
//
// Requires PdfDocumentIntelligenceAnalyzer.IncludeLines - returns no warnings (not "clean",
// just uninformative) when Lines is empty.
//
// Thresholds are a starting point, not calibrated against the real corpus yet - same caveat
// DocumentIdentityResolver's clustering thresholds carry. Revisit once run against the 51-document
// corpus and checked by hand against known over-firing (Buddy) and under-firing (Checklist)
// cases.
internal static class GetFontSizeWarningsHelper
{
    // Below this ratio-to-baseline, a heading-role line isn't convincingly larger than body
    // text - a real heading is rarely this close to body-text size.
    private const double UnderSizedHeadingRatio = 1.15;

    // Above this ratio-to-baseline, an untagged line is convincingly heading-sized rather
    // than just a large body word or a stray big number/price.
    private const double OversizedUntaggedLineRatio = 1.5;

    public static IReadOnlyList<AnalysisWarning> GetFontSizeWarnings(
        IReadOnlyList<Heading> headings, IReadOnlyList<LineInfo> lines, string blobName)
    {
        if (lines.Count == 0)
            return []; // IncludeLines off, or DI returned no lines for this document.

        var heights = lines.Select(LineHeight).Where(h => h > 0).OrderBy(h => h).ToList();
        if (heights.Count == 0)
            return [];

        var baseline = Median(heights);
        if (baseline <= 0)
            return [];

        // Approximate span for each heading - Offset to Offset+Content.Length. Whitespace
        // normalization can make this a few characters off DI's real underlying span, but
        // close enough to tell "this line belongs to this heading paragraph" from "it
        // doesn't" - the only thing either direction below needs.
        var headingRanges = headings
            .Where(h => h.Offset.HasValue)
            .Select(h => (Start: h.Offset!.Value, End: h.Offset!.Value + h.Content.Length, Heading: h))
            .ToList();

        var warnings = new List<AnalysisWarning>();

        var underSized = headingRanges
            .Select(r => (r.Heading, Line: lines
                .Where(l => l.PageNumber == r.Heading.PageNumber && l.Offset is { } o && o >= r.Start && o < r.End)
                .OrderBy(l => l.Offset)
                .FirstOrDefault()))
            .Where(x => x.Line is not null && LineHeight(x.Line!) / baseline < UnderSizedHeadingRatio)
            .Select(x => Truncate(x.Heading.Content))
            .ToList();

        if (underSized.Count > 0)
            warnings.Add(new AnalysisWarning(
                "HeadingFontSizeBelowBaseline",
                $"{underSized.Count} of {headings.Count} heading(s) render no larger than this document's body-text " +
                $"baseline, e.g. {string.Join(", ", underSized.Take(3))} - a possible over-firing candidate DI's own " +
                "Role classification didn't catch.",
                blobName));

        var oversizedUntagged = lines
            .Where(l => l.Offset is { } o && !headingRanges.Any(r => o >= r.Start && o < r.End))
            .Where(l => LineHeight(l) / baseline >= OversizedUntaggedLineRatio)
            .Select(l => Truncate(l.Content.Trim()))
            .Where(c => c.Length > 0)
            .Distinct()
            .ToList();

        if (oversizedUntagged.Count > 0)
            warnings.Add(new AnalysisWarning(
                "UntaggedLargeFontLine",
                $"{oversizedUntagged.Count} line(s) render at {OversizedUntaggedLineRatio}x+ this document's body-text " +
                $"baseline but aren't part of any detected heading, e.g. {string.Join(", ", oversizedUntagged.Take(3))} " +
                "- a possible heading DI's own Role classification missed.",
                blobName));

        return warnings;
    }

    // Bounding-box height from the polygon's Y-extent - robust to point ordering (doesn't
    // assume a specific corner order like top-left-first), not just top/bottom by index.
    private static float LineHeight(LineInfo line)
    {
        if (line.Polygon.Count == 0) return 0;
        var ys = line.Polygon.Select(p => p.Y).ToList();
        return ys.Max() - ys.Min();
    }

    private static double Median(List<float> sortedValues)
    {
        var mid = sortedValues.Count / 2;
        return sortedValues.Count % 2 == 0
            ? (sortedValues[mid - 1] + sortedValues[mid]) / 2.0
            : sortedValues[mid];
    }

    private static string Truncate(string content) =>
        content.Length > 40 ? content[..40] + "…" : content;
}
