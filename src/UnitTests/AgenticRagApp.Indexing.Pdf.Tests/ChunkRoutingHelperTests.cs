using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Indexing.Pdf.Services;

namespace RagApp.UnitTests.PdfExtraction;

[TestClass]
public class ChunkRoutingHelperTests
{
    private static PdfPageRecord Page(int pageNumber, string content) =>
        new() { BlobName = "doc.pdf", PageNumber = pageNumber, PageContent = content, Title = "doc" };

    private static FigureInfo Figure(int pageNumber) =>
        new(Caption: null, Offset: 0, PageNumber: pageNumber, Id: "1", Elements: []);

    private static Heading Heading(string content, int? offset, int pageNumber = 0, string role = "sectionHeading") =>
        new(content, role, offset, pageNumber);

    private static SelectionMarkInfo SelectionMark(int pageNumber) =>
        new(pageNumber, "unselected", Offset: 0, Confidence: 1.0, Polygon: []);

    [TestMethod]
    public void MidSizedDocument_HasExtractableContent()
    {
        // 10 pages, 2,000 chars each (20,000 chars total): well above the 1,000 chars/page
        // sparse threshold, small file relative to text so bytes/char stays low.
        var pages = Enumerable.Range(1, 10).Select(n => Page(n, new string('a', 2_000))).ToList();

        var routing = ChunkRoutingHelper.Compute(pages, [], fileSizeBytes: 5_000);

        Assert.IsTrue(routing.HasExtractableContent);
        Assert.AreEqual(2_000, routing.CharsPerPage);
    }

    [TestMethod]
    public void SparseCharsPerPage_FailsExtractionGate_EvenAtNormalByteRatio()
    {
        // 500 chars/page - below the 1,000 threshold - regardless of page count or byte ratio.
        var pages = Enumerable.Range(1, 5).Select(n => Page(n, new string('a', 500))).ToList();

        var routing = ChunkRoutingHelper.Compute(pages, [], fileSizeBytes: 1_000);

        Assert.IsFalse(routing.HasExtractableContent);
    }

    [TestMethod]
    public void HighByteRatio_FailsExtractionGate_EvenAtNormalCharsPerPage()
    {
        // 2,000 chars/page (well above sparse), but a large file size relative to extracted
        // text - the extraction-loss signal, e.g. an image-heavy scan with a thin text layer.
        var pages = Enumerable.Range(1, 5).Select(n => Page(n, new string('a', 2_000))).ToList();

        var routing = ChunkRoutingHelper.Compute(pages, [], fileSizeBytes: 2_000_000);

        Assert.IsFalse(routing.HasExtractableContent);
    }

    [TestMethod]
    public void CharsPerPageJustAtThreshold_1000_PassesExtractionGate()
    {
        // Exactly 1,000 chars/page - the rule is "< 1,000", so 1,000 itself must not trip Picture.
        var pages = new[] { Page(1, new string('a', 1_000)) };

        var routing = ChunkRoutingHelper.Compute(pages, [], fileSizeBytes: 1_000);

        Assert.IsTrue(routing.HasExtractableContent);
    }

    [TestMethod]
    public void CharsPerPageJustBelowThreshold_999_FailsExtractionGate()
    {
        var pages = new[] { Page(1, new string('a', 999)) };

        var routing = ChunkRoutingHelper.Compute(pages, [], fileSizeBytes: 999);

        Assert.IsFalse(routing.HasExtractableContent);
    }

    [TestMethod]
    public void BytesPerCharJustAtThreshold_100_FailsExtractionGate()
    {
        // Rule is ">= 100", so exactly 100 must trip Picture.
        var pages = new[] { Page(1, new string('a', 1_000)) };

        var routing = ChunkRoutingHelper.Compute(pages, [], fileSizeBytes: 100_000);

        Assert.IsFalse(routing.HasExtractableContent);
        Assert.AreEqual(100, routing.BytesPerChar);
    }

    [TestMethod]
    public void BytesPerCharJustBelowThreshold_PassesExtractionGate()
    {
        var pages = new[] { Page(1, new string('a', 1_000)) };

        var routing = ChunkRoutingHelper.Compute(pages, [], fileSizeBytes: 99_000);

        Assert.IsTrue(routing.HasExtractableContent);
    }

    [TestMethod]
    public void LargeDocument_StillReportsEstimatedTokens_ButNoSizeTier()
    {
        // 30 pages, 6,000 chars each (180,000 chars total) -> ~58,065 estimated tokens at
        // the prose ratio. EstimatedTokens is still measured and carried; what is gone is
        // the Large/Medium/Small tier it used to select. Decision 2 answers "is the whole
        // document a safe return unit" against a MEASURED return bound instead, and that
        // bound does not exist yet - see DocumentIsSafeReturnUnit.
        var pages = Enumerable.Range(1, 30).Select(n => Page(n, new string('a', 6_000))).ToList();

        var routing = ChunkRoutingHelper.Compute(pages, [], fileSizeBytes: 200_000);

        Assert.IsTrue(routing.EstimatedTokens >= 50_000);
        Assert.IsTrue(routing.HasExtractableContent);
        Assert.IsNull(routing.DocumentIsSafeReturnUnit);
    }

    [TestMethod]
    public void SmallDocument_StillReportsEstimatedTokens_ButNoSizeTier()
    {
        // 2 pages, 2,000 chars each (4,000 chars total) -> ~1,291 estimated tokens.
        var pages = Enumerable.Range(1, 2).Select(n => Page(n, new string('a', 2_000))).ToList();

        var routing = ChunkRoutingHelper.Compute(pages, [], fileSizeBytes: 4_000);

        Assert.IsTrue(routing.EstimatedTokens < 4_000);
        Assert.IsTrue(routing.HasExtractableContent);
        Assert.IsNull(routing.DocumentIsSafeReturnUnit);
    }

    [TestMethod]
    public void NavigationSummary_DrivenByHeadingCount_NotTokenCount()
    {
        // Decision 3 is a section-count question: a document needs navigation when its
        // sections compete against each other in a flat ranking. A short document with many
        // headings needs it; a long one with few does not.
        var pages    = Enumerable.Range(1, 2).Select(n => Page(n, new string('a', 2_000))).ToList();
        var headings = Enumerable.Range(0, 120).Select(i => Heading($"H{i}", i * 30)).ToList();

        var many = ChunkRoutingHelper.Compute(pages, [], fileSizeBytes: 4_000, headings: headings);
        var few  = ChunkRoutingHelper.Compute(pages, [], fileSizeBytes: 4_000, headings: [Heading("Only", 0)]);

        Assert.IsTrue(many.NeedsNavigationSummary);
        Assert.IsFalse(few.NeedsNavigationSummary);
    }

    [TestMethod]
    public void EstimatedTokens_ComputedFromProseRatio_NotJustCharsPerPage()
    {
        var pages = new[] { Page(1, new string('a', 3_100)) };

        var routing = ChunkRoutingHelper.Compute(pages, [], fileSizeBytes: 3_100);

        // ceil(3,100 / 3.1) = 1,000 - exact round number picked so the assertion doesn't
        // itself depend on rounding behaviour.
        Assert.AreEqual(1_000, routing.EstimatedTokens);
    }

    [TestMethod]
    public void ZeroPages_DoesNotThrow_FailsExtractionGate()
    {
        var routing = ChunkRoutingHelper.Compute([], [], fileSizeBytes: 100);

        Assert.IsFalse(routing.HasExtractableContent);
        Assert.AreEqual(0, routing.CharsPerPage);
    }

    [TestMethod]
    public void ZeroExtractedChars_NonZeroFileSize_DoesNotThrow_FailsExtractionGate()
    {
        var pages = new[] { Page(1, "") };

        var routing = ChunkRoutingHelper.Compute(pages, [], fileSizeBytes: 5_000);

        Assert.IsFalse(routing.HasExtractableContent);
        Assert.IsTrue(double.IsPositiveInfinity(routing.BytesPerChar));
    }

    [TestMethod]
    public void FiguresPerPage_IsComputedFromFigureCount()
    {
        var pages   = Enumerable.Range(1, 4).Select(n => Page(n, new string('a', 2_000))).ToList();
        var figures = new[] { Figure(1), Figure(1), Figure(3) };

        var routing = ChunkRoutingHelper.Compute(pages, figures, fileSizeBytes: 10_000);

        Assert.AreEqual(0.75, routing.FiguresPerPage);
    }

    [TestMethod]
    public void RawCountsAreCarriedThroughUnmodified()
    {
        var pages = new[] { Page(1, new string('a', 1_500)), Page(2, new string('a', 2_500)) };

        var routing = ChunkRoutingHelper.Compute(pages, [], fileSizeBytes: 12_000);

        Assert.AreEqual(2, routing.ExtractedPageCount);
        Assert.AreEqual(4_000, routing.TotalChars);
        Assert.AreEqual(12_000, routing.FileSizeBytes);
    }

    // ── B3 - headings per 1,000 chars ────────────────────────────────────────

    [TestMethod]
    public void HeadingsPerThousandChars_ComputedFromHeadingCountAndTotalChars()
    {
        var pages    = new[] { Page(1, new string('a', 2_000)) };
        var headings = new[] { Heading("A", 0), Heading("B", 10), Heading("C", 20), Heading("D", 30) };

        var routing = ChunkRoutingHelper.Compute(pages, [], fileSizeBytes: 2_000, headings: headings);

        Assert.AreEqual(2.0, routing.HeadingsPerThousandChars);
    }

    [TestMethod]
    public void HeadingsPerThousandChars_NoHeadings_IsZero()
    {
        var pages = new[] { Page(1, new string('a', 2_000)) };

        var routing = ChunkRoutingHelper.Compute(pages, [], fileSizeBytes: 2_000);

        Assert.AreEqual(0, routing.HeadingsPerThousandChars);
    }

    [TestMethod]
    public void HeadingsPerThousandChars_ZeroChars_DoesNotThrow_IsZero()
    {
        var pages = new[] { Page(1, "") };

        var routing = ChunkRoutingHelper.Compute(pages, [], fileSizeBytes: 0, headings: [Heading("A", 0)]);

        Assert.AreEqual(0, routing.HeadingsPerThousandChars);
    }

    // ── B4 - numbered-heading share ──────────────────────────────────────────

    [TestMethod]
    public void NumberedHeadingShare_MixOfNumberedAndNot_ComputesRatio()
    {
        var pages    = new[] { Page(1, new string('a', 1_000)) };
        var headings = new[]
        {
            Heading("1.1 Voedselveiligheid", 0),
            Heading("Artikel 9 Vakantie", 20),
            Heading("Definities", 40),          // not numbered
            Heading("Scope", 50),                // not numbered
        };

        var routing = ChunkRoutingHelper.Compute(pages, [], fileSizeBytes: 1_000, headings: headings);

        Assert.AreEqual(0.5, routing.NumberedHeadingShare);
    }

    [TestMethod]
    public void NumberedHeadingShare_NoHeadings_IsZero()
    {
        var pages = new[] { Page(1, new string('a', 1_000)) };

        var routing = ChunkRoutingHelper.Compute(pages, [], fileSizeBytes: 1_000);

        Assert.AreEqual(0, routing.NumberedHeadingShare);
    }

    [TestMethod]
    public void NumberedHeadingShare_AllTopicHeadings_IsZero()
    {
        var pages    = new[] { Page(1, new string('a', 1_000)) };
        var headings = new[] { Heading("Definities", 0), Heading("Scope", 20) };

        var routing = ChunkRoutingHelper.Compute(pages, [], fileSizeBytes: 1_000, headings: headings);

        Assert.AreEqual(0, routing.NumberedHeadingShare);
    }

    // ── B5 - max section size (largest gap between headings) ────────────────

    [TestMethod]
    public void MaxSectionSizeChars_NoHeadings_IsWholeDocument()
    {
        var pages = new[] { Page(1, new string('a', 2_668)) };

        var routing = ChunkRoutingHelper.Compute(pages, [], fileSizeBytes: 2_668);

        Assert.AreEqual(2_668, routing.MaxSectionSizeChars);
    }

    [TestMethod]
    public void MaxSectionSizeChars_EvenlySpacedHeadings_IsTheEqualGap()
    {
        // Headings at 0, 100, 200 in a 300-char document - three equal 100-char gaps
        // (0->100, 100->200, 200->300), so the max is 100, not the document total.
        var pages    = new[] { Page(1, new string('a', 300)) };
        var headings = new[] { Heading("A", 0), Heading("B", 100), Heading("C", 200) };

        var routing = ChunkRoutingHelper.Compute(pages, [], fileSizeBytes: 300, headings: headings);

        Assert.AreEqual(100, routing.MaxSectionSizeChars);
    }

    [TestMethod]
    public void MaxSectionSizeChars_OneLargeGapAmongSmallOnes_FindsTheLargestGap()
    {
        // Gaps: 0->10 (10), 10->20 (10), 20->900 (880, the real outlier), 900->1000 (100).
        var pages    = new[] { Page(1, new string('a', 1_000)) };
        var headings = new[] { Heading("A", 10), Heading("B", 20), Heading("C", 900) };

        var routing = ChunkRoutingHelper.Compute(pages, [], fileSizeBytes: 1_000, headings: headings);

        Assert.AreEqual(880, routing.MaxSectionSizeChars);
    }

    [TestMethod]
    public void MaxSectionSizeChars_HeadingsWithNullOffset_AreIgnored()
    {
        var pages    = new[] { Page(1, new string('a', 500)) };
        var headings = new[] { Heading("A", 0), Heading("Unknown offset", null), Heading("B", 250) };

        var routing = ChunkRoutingHelper.Compute(pages, [], fileSizeBytes: 500, headings: headings);

        Assert.AreEqual(250, routing.MaxSectionSizeChars);
    }

    // ── A2 - boilerplate share ───────────────────────────────────────────────

    [TestMethod]
    public void BoilerplateShare_ComputedFromBoilerplateCharsOverTotalChars()
    {
        var pages       = new[] { Page(1, new string('a', 1_000)) };
        var boilerplate = new[] { Heading(new string('b', 100), 0, role: "pageHeader"), Heading(new string('b', 150), 0, role: "pageFooter") };

        var routing = ChunkRoutingHelper.Compute(pages, [], fileSizeBytes: 1_000, boilerplate: boilerplate);

        Assert.AreEqual(0.25, routing.BoilerplateShare);
    }

    [TestMethod]
    public void BoilerplateShare_NoBoilerplate_IsZero()
    {
        var pages = new[] { Page(1, new string('a', 1_000)) };

        var routing = ChunkRoutingHelper.Compute(pages, [], fileSizeBytes: 1_000);

        Assert.AreEqual(0, routing.BoilerplateShare);
    }

    [TestMethod]
    public void BoilerplateShare_ZeroChars_DoesNotThrow_IsZero()
    {
        var pages = new[] { Page(1, "") };

        var routing = ChunkRoutingHelper.Compute(pages, [], fileSizeBytes: 0, boilerplate: [Heading("b", 0, role: "pageFooter")]);

        Assert.AreEqual(0, routing.BoilerplateShare);
    }

    // ── A5 - selection marks per page ────────────────────────────────────────

    [TestMethod]
    public void SelectionMarksPerPage_ComputedFromSelectionMarkCount()
    {
        var pages          = Enumerable.Range(1, 4).Select(n => Page(n, new string('a', 2_000))).ToList();
        var selectionMarks = new[] { SelectionMark(1), SelectionMark(1), SelectionMark(1) };

        var routing = ChunkRoutingHelper.Compute(pages, [], fileSizeBytes: 8_000, selectionMarks: selectionMarks);

        Assert.AreEqual(0.75, routing.SelectionMarksPerPage);
    }

    [TestMethod]
    public void SelectionMarksPerPage_NoSelectionMarks_IsZero()
    {
        var pages = new[] { Page(1, new string('a', 2_000)) };

        var routing = ChunkRoutingHelper.Compute(pages, [], fileSizeBytes: 2_000);

        Assert.AreEqual(0, routing.SelectionMarksPerPage);
    }

    [TestMethod]
    public void SelectionMarksPerPage_ZeroPages_DoesNotThrow_IsZero()
    {
        var routing = ChunkRoutingHelper.Compute([], [], fileSizeBytes: 0, selectionMarks: [SelectionMark(1)]);

        Assert.AreEqual(0, routing.SelectionMarksPerPage);
    }
}
