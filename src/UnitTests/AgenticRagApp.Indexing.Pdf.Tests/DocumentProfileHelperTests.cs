using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Indexing.Pdf.Services;

namespace RagApp.UnitTests.PdfExtraction;

[TestClass]
public class DocumentProfileHelperTests
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
    public void TableCharShare_MeasuresTableBlockFraction()
    {
        // One markdown table block (>= 2 consecutive row lines - the same rule
        // SplitIntoBlocks applies at chunk time) inside surrounding prose.
        var table = "| kolom a | kolom b |\n| 10 | 20 |";
        var prose = new string('a', 2_000);
        var pages = new List<PdfPageRecord> { Page(1, $"{prose}\n{table}") };

        var profile = DocumentProfileHelper.Compute(pages, [], fileSizeBytes: 1_000);

        Assert.IsTrue(profile.TableCharShare > 0, "table block characters must be counted");
        Assert.IsTrue(profile.TableCharShare < 0.5, "a table sliver must not read as table-shaped");
    }

    [TestMethod]
    public void TableCharShare_TableDominatedDocument_IsAboveHalf()
    {
        var rows  = string.Join("\n", Enumerable.Range(1, 60).Select(n => $"| rij {n} | waarde {n} |"));
        var pages = new List<PdfPageRecord> { Page(1, $"korte inleiding\n{rows}") };

        var profile = DocumentProfileHelper.Compute(pages, [], fileSizeBytes: 1_000);

        Assert.IsTrue(profile.TableCharShare >= 0.5,
            $"a table-dominated document must clear the table-shaped bar (was {profile.TableCharShare:F2})");
    }

    [TestMethod]
    public void TableCharShare_NoTables_IsZero()
    {
        var pages = new List<PdfPageRecord> { Page(1, new string('a', 2_000)) };

        var profile = DocumentProfileHelper.Compute(pages, [], fileSizeBytes: 1_000);

        Assert.AreEqual(0, profile.TableCharShare);
    }

    [TestMethod]
    public void MidSizedDocument_HasExtractableContent()
    {
        // 10 pages, 2,000 chars each (20,000 chars total): well above the 1,000 chars/page
        // sparse threshold, small file relative to text so bytes/char stays low.
        var pages = Enumerable.Range(1, 10).Select(n => Page(n, new string('a', 2_000))).ToList();

        var profile = DocumentProfileHelper.Compute(pages, [], fileSizeBytes: 5_000);

        Assert.IsTrue(profile.HasExtractableContent);
        Assert.AreEqual(2_000, profile.CharsPerPage);
    }

    [TestMethod]
    public void SparseCharsPerPage_FailsExtractionGate_EvenAtNormalByteRatio()
    {
        // 500 chars/page - below the 1,000 threshold - regardless of page count or byte ratio.
        var pages = Enumerable.Range(1, 5).Select(n => Page(n, new string('a', 500))).ToList();

        var profile = DocumentProfileHelper.Compute(pages, [], fileSizeBytes: 1_000);

        Assert.IsFalse(profile.HasExtractableContent);
    }

    [TestMethod]
    public void HighByteRatio_FailsExtractionGate_EvenAtNormalCharsPerPage()
    {
        // 2,000 chars/page (well above sparse), but a large file size relative to extracted
        // text - the extraction-loss signal, e.g. an image-heavy scan with a thin text layer.
        var pages = Enumerable.Range(1, 5).Select(n => Page(n, new string('a', 2_000))).ToList();

        var profile = DocumentProfileHelper.Compute(pages, [], fileSizeBytes: 2_000_000);

        Assert.IsFalse(profile.HasExtractableContent);
    }

    [TestMethod]
    public void CharsPerPageJustAtThreshold_1000_PassesExtractionGate()
    {
        // Exactly 1,000 chars/page - the rule is "< 1,000", so 1,000 itself must not trip Picture.
        var pages = new[] { Page(1, new string('a', 1_000)) };

        var profile = DocumentProfileHelper.Compute(pages, [], fileSizeBytes: 1_000);

        Assert.IsTrue(profile.HasExtractableContent);
    }

    [TestMethod]
    public void CharsPerPageJustBelowThreshold_999_FailsExtractionGate()
    {
        var pages = new[] { Page(1, new string('a', 999)) };

        var profile = DocumentProfileHelper.Compute(pages, [], fileSizeBytes: 999);

        Assert.IsFalse(profile.HasExtractableContent);
    }

    [TestMethod]
    public void BytesPerCharJustAtThreshold_100_FailsExtractionGate()
    {
        // Rule is ">= 100", so exactly 100 must trip Picture.
        var pages = new[] { Page(1, new string('a', 1_000)) };

        var profile = DocumentProfileHelper.Compute(pages, [], fileSizeBytes: 100_000);

        Assert.IsFalse(profile.HasExtractableContent);
        Assert.AreEqual(100, profile.BytesPerChar);
    }

    [TestMethod]
    public void BytesPerCharJustBelowThreshold_PassesExtractionGate()
    {
        var pages = new[] { Page(1, new string('a', 1_000)) };

        var profile = DocumentProfileHelper.Compute(pages, [], fileSizeBytes: 99_000);

        Assert.IsTrue(profile.HasExtractableContent);
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

        var profile = DocumentProfileHelper.Compute(pages, [], fileSizeBytes: 200_000);

        Assert.IsTrue(profile.EstimatedTokens >= 50_000);
        Assert.IsTrue(profile.HasExtractableContent);
        Assert.IsNull(profile.DocumentIsSafeReturnUnit);
    }

    [TestMethod]
    public void SmallDocument_StillReportsEstimatedTokens_ButNoSizeTier()
    {
        // 2 pages, 2,000 chars each (4,000 chars total) -> ~1,291 estimated tokens.
        var pages = Enumerable.Range(1, 2).Select(n => Page(n, new string('a', 2_000))).ToList();

        var profile = DocumentProfileHelper.Compute(pages, [], fileSizeBytes: 4_000);

        Assert.IsTrue(profile.EstimatedTokens < 4_000);
        Assert.IsTrue(profile.HasExtractableContent);
        Assert.IsNull(profile.DocumentIsSafeReturnUnit);
    }

    [TestMethod]
    public void NavigationSummary_DrivenByHeadingCount_NotTokenCount()
    {
        // Decision 3 is a section-count question: a document needs navigation when its
        // sections compete against each other in a flat ranking. A short document with many
        // headings needs it; a long one with few does not.
        var pages    = Enumerable.Range(1, 2).Select(n => Page(n, new string('a', 2_000))).ToList();
        var headings = Enumerable.Range(0, 120).Select(i => Heading($"H{i}", i * 30)).ToList();

        var many = DocumentProfileHelper.Compute(pages, [], fileSizeBytes: 4_000, headings: headings);
        var few  = DocumentProfileHelper.Compute(pages, [], fileSizeBytes: 4_000, headings: [Heading("Only", 0)]);

        Assert.IsTrue(many.NeedsNavigationSummary);
        Assert.IsFalse(few.NeedsNavigationSummary);
    }

    [TestMethod]
    public void EstimatedTokens_ComputedFromProseRatio_NotJustCharsPerPage()
    {
        var pages = new[] { Page(1, new string('a', 3_100)) };

        var profile = DocumentProfileHelper.Compute(pages, [], fileSizeBytes: 3_100);

        // ceil(3,100 / 3.1) = 1,000 - exact round number picked so the assertion doesn't
        // itself depend on rounding behaviour.
        Assert.AreEqual(1_000, profile.EstimatedTokens);
    }

    [TestMethod]
    public void ZeroPages_DoesNotThrow_FailsExtractionGate()
    {
        var profile = DocumentProfileHelper.Compute([], [], fileSizeBytes: 100);

        Assert.IsFalse(profile.HasExtractableContent);
        Assert.AreEqual(0, profile.CharsPerPage);
    }

    [TestMethod]
    public void ZeroExtractedChars_NonZeroFileSize_DoesNotThrow_FailsExtractionGate()
    {
        var pages = new[] { Page(1, "") };

        var profile = DocumentProfileHelper.Compute(pages, [], fileSizeBytes: 5_000);

        Assert.IsFalse(profile.HasExtractableContent);
        Assert.IsTrue(double.IsPositiveInfinity(profile.BytesPerChar));
    }

    [TestMethod]
    public void FiguresPerPage_IsComputedFromFigureCount()
    {
        var pages   = Enumerable.Range(1, 4).Select(n => Page(n, new string('a', 2_000))).ToList();
        var figures = new[] { Figure(1), Figure(1), Figure(3) };

        var profile = DocumentProfileHelper.Compute(pages, figures, fileSizeBytes: 10_000);

        Assert.AreEqual(0.75, profile.FiguresPerPage);
    }

    [TestMethod]
    public void RawCountsAreCarriedThroughUnmodified()
    {
        var pages = new[] { Page(1, new string('a', 1_500)), Page(2, new string('a', 2_500)) };

        var profile = DocumentProfileHelper.Compute(pages, [], fileSizeBytes: 12_000);

        Assert.AreEqual(2, profile.ExtractedPageCount);
        Assert.AreEqual(4_000, profile.TotalChars);
        Assert.AreEqual(12_000, profile.FileSizeBytes);
    }

    // ── B3 - headings per 1,000 chars ────────────────────────────────────────

    [TestMethod]
    public void HeadingsPerThousandChars_ComputedFromHeadingCountAndTotalChars()
    {
        var pages    = new[] { Page(1, new string('a', 2_000)) };
        var headings = new[] { Heading("A", 0), Heading("B", 10), Heading("C", 20), Heading("D", 30) };

        var profile = DocumentProfileHelper.Compute(pages, [], fileSizeBytes: 2_000, headings: headings);

        Assert.AreEqual(2.0, profile.HeadingsPerThousandChars);
    }

    [TestMethod]
    public void HeadingsPerThousandChars_NoHeadings_IsZero()
    {
        var pages = new[] { Page(1, new string('a', 2_000)) };

        var profile = DocumentProfileHelper.Compute(pages, [], fileSizeBytes: 2_000);

        Assert.AreEqual(0, profile.HeadingsPerThousandChars);
    }

    [TestMethod]
    public void HeadingsPerThousandChars_ZeroChars_DoesNotThrow_IsZero()
    {
        var pages = new[] { Page(1, "") };

        var profile = DocumentProfileHelper.Compute(pages, [], fileSizeBytes: 0, headings: [Heading("A", 0)]);

        Assert.AreEqual(0, profile.HeadingsPerThousandChars);
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

        var profile = DocumentProfileHelper.Compute(pages, [], fileSizeBytes: 1_000, headings: headings);

        Assert.AreEqual(0.5, profile.NumberedHeadingShare);
    }

    [TestMethod]
    public void NumberedHeadingShare_NoHeadings_IsZero()
    {
        var pages = new[] { Page(1, new string('a', 1_000)) };

        var profile = DocumentProfileHelper.Compute(pages, [], fileSizeBytes: 1_000);

        Assert.AreEqual(0, profile.NumberedHeadingShare);
    }

    [TestMethod]
    public void NumberedHeadingShare_AllTopicHeadings_IsZero()
    {
        var pages    = new[] { Page(1, new string('a', 1_000)) };
        var headings = new[] { Heading("Definities", 0), Heading("Scope", 20) };

        var profile = DocumentProfileHelper.Compute(pages, [], fileSizeBytes: 1_000, headings: headings);

        Assert.AreEqual(0, profile.NumberedHeadingShare);
    }

    // ── B5 - max section size (largest gap between headings) ────────────────

    [TestMethod]
    public void MaxSectionSizeChars_NoHeadings_IsWholeDocument()
    {
        var pages = new[] { Page(1, new string('a', 2_668)) };

        var profile = DocumentProfileHelper.Compute(pages, [], fileSizeBytes: 2_668);

        Assert.AreEqual(2_668, profile.MaxSectionSizeChars);
    }

    [TestMethod]
    public void MaxSectionSizeChars_EvenlySpacedHeadings_IsTheEqualGap()
    {
        // Headings at 0, 100, 200 in a 300-char document - three equal 100-char gaps
        // (0->100, 100->200, 200->300), so the max is 100, not the document total.
        var pages    = new[] { Page(1, new string('a', 300)) };
        var headings = new[] { Heading("A", 0), Heading("B", 100), Heading("C", 200) };

        var profile = DocumentProfileHelper.Compute(pages, [], fileSizeBytes: 300, headings: headings);

        Assert.AreEqual(100, profile.MaxSectionSizeChars);
    }

    [TestMethod]
    public void MaxSectionSizeChars_OneLargeGapAmongSmallOnes_FindsTheLargestGap()
    {
        // Gaps: 0->10 (10), 10->20 (10), 20->900 (880, the real outlier), 900->1000 (100).
        var pages    = new[] { Page(1, new string('a', 1_000)) };
        var headings = new[] { Heading("A", 10), Heading("B", 20), Heading("C", 900) };

        var profile = DocumentProfileHelper.Compute(pages, [], fileSizeBytes: 1_000, headings: headings);

        Assert.AreEqual(880, profile.MaxSectionSizeChars);
    }

    [TestMethod]
    public void MaxSectionSizeChars_HeadingsWithNullOffset_AreIgnored()
    {
        var pages    = new[] { Page(1, new string('a', 500)) };
        var headings = new[] { Heading("A", 0), Heading("Unknown offset", null), Heading("B", 250) };

        var profile = DocumentProfileHelper.Compute(pages, [], fileSizeBytes: 500, headings: headings);

        Assert.AreEqual(250, profile.MaxSectionSizeChars);
    }

    // ── A2 - boilerplate share ───────────────────────────────────────────────

    [TestMethod]
    public void BoilerplateShare_ComputedFromBoilerplateCharsOverTotalChars()
    {
        var pages       = new[] { Page(1, new string('a', 1_000)) };
        var boilerplate = new[] { Heading(new string('b', 100), 0, role: "pageHeader"), Heading(new string('b', 150), 0, role: "pageFooter") };

        var profile = DocumentProfileHelper.Compute(pages, [], fileSizeBytes: 1_000, boilerplate: boilerplate);

        Assert.AreEqual(0.25, profile.BoilerplateShare);
    }

    [TestMethod]
    public void BoilerplateShare_NoBoilerplate_IsZero()
    {
        var pages = new[] { Page(1, new string('a', 1_000)) };

        var profile = DocumentProfileHelper.Compute(pages, [], fileSizeBytes: 1_000);

        Assert.AreEqual(0, profile.BoilerplateShare);
    }

    [TestMethod]
    public void BoilerplateShare_ZeroChars_DoesNotThrow_IsZero()
    {
        var pages = new[] { Page(1, "") };

        var profile = DocumentProfileHelper.Compute(pages, [], fileSizeBytes: 0, boilerplate: [Heading("b", 0, role: "pageFooter")]);

        Assert.AreEqual(0, profile.BoilerplateShare);
    }

    // ── A5 - selection marks per page ────────────────────────────────────────

    [TestMethod]
    public void SelectionMarksPerPage_ComputedFromSelectionMarkCount()
    {
        var pages          = Enumerable.Range(1, 4).Select(n => Page(n, new string('a', 2_000))).ToList();
        var selectionMarks = new[] { SelectionMark(1), SelectionMark(1), SelectionMark(1) };

        var profile = DocumentProfileHelper.Compute(pages, [], fileSizeBytes: 8_000, selectionMarks: selectionMarks);

        Assert.AreEqual(0.75, profile.SelectionMarksPerPage);
    }

    [TestMethod]
    public void SelectionMarksPerPage_NoSelectionMarks_IsZero()
    {
        var pages = new[] { Page(1, new string('a', 2_000)) };

        var profile = DocumentProfileHelper.Compute(pages, [], fileSizeBytes: 2_000);

        Assert.AreEqual(0, profile.SelectionMarksPerPage);
    }

    [TestMethod]
    public void SelectionMarksPerPage_ZeroPages_DoesNotThrow_IsZero()
    {
        var profile = DocumentProfileHelper.Compute([], [], fileSizeBytes: 0, selectionMarks: [SelectionMark(1)]);

        Assert.AreEqual(0, profile.SelectionMarksPerPage);
    }
}
