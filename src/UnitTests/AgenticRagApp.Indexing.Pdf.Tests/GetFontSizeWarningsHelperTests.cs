using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Indexing.Pdf.Services;

namespace RagApp.UnitTests.PdfExtraction;

[TestClass]
public class GetFontSizeWarningsHelperTests
{
    // A line's Offset must exactly cover the heading's [Offset, Offset+Content.Length) range
    // for GetFontSizeWarningsHelper to treat it as "this heading's own line" - offset alone,
    // same page, height is whatever the polygon's Y-extent works out to.
    private static LineInfo Line(string content, int offset, int page, float height, float y0 = 0f) =>
        new(content, offset, page,
            [new PolygonPoint(0, y0), new PolygonPoint(1, y0), new PolygonPoint(1, y0 + height), new PolygonPoint(0, y0 + height)]);

    private static Heading Heading(string content, int offset, int page) =>
        new(content, "sectionHeading", offset, page);

    [TestMethod]
    public void GetFontSizeWarnings_NoLines_ReturnsEmpty()
    {
        var result = GetFontSizeWarningsHelper.GetFontSizeWarnings([Heading("Title", 0, 0)], [], "doc.pdf");

        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void GetFontSizeWarnings_HeadingLineWellAboveBaseline_NoWarning()
    {
        // 9 body lines at height 10 (baseline = 10), one heading line at height 20 (2x) -
        // convincingly larger, no HeadingFontSizeBelowBaseline warning.
        var body = Enumerable.Range(0, 9).Select(i => Line($"body{i}", 100 + i, 0, 10f)).ToList();
        var headingLine = Line("Big Heading", 0, 0, 20f);
        var lines = body.Append(headingLine).ToList();
        var headings = new[] { Heading("Big Heading", 0, 0) };

        var result = GetFontSizeWarningsHelper.GetFontSizeWarnings(headings, lines, "doc.pdf");

        Assert.IsFalse(result.Any(w => w.Code == "HeadingFontSizeBelowBaseline"));
    }

    [TestMethod]
    public void GetFontSizeWarnings_HeadingLineSameHeightAsBaseline_WarnsBelowBaseline()
    {
        // Every line, including the "heading," renders at the same height - the Buddy
        // over-firing shape: a heading-role paragraph that isn't visually larger than body text.
        var body = Enumerable.Range(0, 9).Select(i => Line($"body{i}", 100 + i, 0, 10f)).ToList();
        var headingLine = Line("Not Really A Heading", 0, 0, 10f);
        var lines = body.Append(headingLine).ToList();
        var headings = new[] { Heading("Not Really A Heading", 0, 0) };

        var result = GetFontSizeWarningsHelper.GetFontSizeWarnings(headings, lines, "doc.pdf");

        var warning = result.Single(w => w.Code == "HeadingFontSizeBelowBaseline");
        StringAssert.Contains(warning.Message, "Not Really A Heading");
    }

    [TestMethod]
    public void GetFontSizeWarnings_LargeUntaggedLine_WarnsPossibleMissedHeading()
    {
        // A line rendered well above baseline that no detected heading covers - the
        // "Checklist" miss shape (docs/2608/260811/d1-small-heading-quality-findings.md).
        var body = Enumerable.Range(0, 9).Select(i => Line($"body{i}", 100 + i, 0, 10f)).ToList();
        var untagged = Line("Checklist", 500, 0, 20f);
        var lines = body.Append(untagged).ToList();

        var result = GetFontSizeWarningsHelper.GetFontSizeWarnings([], lines, "doc.pdf");

        var warning = result.Single(w => w.Code == "UntaggedLargeFontLine");
        StringAssert.Contains(warning.Message, "Checklist");
    }

    [TestMethod]
    public void GetFontSizeWarnings_LargeLineInsideHeadingRange_NotFlaggedAsUntagged()
    {
        // The line IS the heading's own line (covered by its Offset range) - must not also
        // fire as an "untagged" possible-missed-heading candidate.
        var body = Enumerable.Range(0, 9).Select(i => Line($"body{i}", 100 + i, 0, 10f)).ToList();
        var headingLine = Line("Big Heading", 0, 0, 20f);
        var lines = body.Append(headingLine).ToList();
        var headings = new[] { Heading("Big Heading", 0, 0) };

        var result = GetFontSizeWarningsHelper.GetFontSizeWarnings(headings, lines, "doc.pdf");

        Assert.IsFalse(result.Any(w => w.Code == "UntaggedLargeFontLine"));
    }

    [TestMethod]
    public void GetFontSizeWarnings_EmptyPolygon_TreatedAsZeroHeightAndSkipped()
    {
        var lineWithNoPolygon = new LineInfo("body", 0, 0, []);

        var result = GetFontSizeWarningsHelper.GetFontSizeWarnings([], [lineWithNoPolygon], "doc.pdf");

        Assert.AreEqual(0, result.Count);
    }
}
