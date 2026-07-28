using Microsoft.VisualStudio.TestTools.UnitTesting;
using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Indexing.Pdf.Services;
using AgenticRagApp.Common.Models;

namespace RagApp.UnitTests.PdfExtraction;

[TestClass]
public class PdfPipelineValidatorTests
{
    private static PdfCleaner           BuildCleaner()   => new();
    private static PdfPipelineValidator BuildValidator() => new();

    private static PdfPageRecord Page(string blobName, string content, int pageIndex = 0, string title = "Title") => new()
    {
        BlobName    = blobName,
        PageNumber  = pageIndex,
        PageContent = content,
        Title       = title,
    };

    private static PdfDocumentStructure Structure(params TableInfo[] tables) => new(
        Headings: [],
        Boilerplate: [],
        Tables: tables,
        PageDimensions: [],
        SelectionMarks: [],
        Figures: [],
        Lines: [],
        Sections: []);

    // One file's worth of PDFExtractionResult, carrying whatever pages/structure the
    // test needs - the validator flattens this (and any other files) internally now,
    // so this is the only fixture shape tests build.
    private static PDFExtractionResult FileResult(
        string blobName, IReadOnlyList<PdfPageRecord> pages, PdfDocumentStructure? structure = null,
        PdfStepDiagnostics? metadataDiagnostics = null) => new(
            Ok:               true,
            BlobName:         blobName,
            FileSizeBytes:    1024,
            PdfSpecVersion:   1.7,
            NativeMetadata:   null,
            RawContent:       null,
            Pages:            pages,
            Structure:        structure,
            EstimatedCostUsd: null,
            Error:            null)
        {
            MetadataDiagnostics = metadataDiagnostics ?? PdfStepDiagnostics.Empty,
        };

    // Builds a clean, all-good pipeline: one page, cleaned, no errors anywhere - the
    // baseline every test below perturbs one piece of.
    private static (IReadOnlyList<PDFExtractionResult> FileResults, PdfCleanResult Clean) HappyPath()
    {
        var page        = Page("doc1.pdf", "## Heading\nSome markdown content");
        var fileResults = new[] { FileResult("doc1.pdf", [page]) };
        var clean       = BuildCleaner().CleanPdf([page]);
        return (fileResults, clean);
    }

    [TestMethod]
    public void HappyPath_Passes()
    {
        var (fileResults, clean) = HappyPath();

        var report = BuildValidator().Validate(fileResults, clean);

        Assert.IsTrue(report.Passed);
        Assert.AreEqual(0, report.ReconciliationProblems.Count);
    }

    [TestMethod]
    public void NoInputAtAll_FailsRatherThanPassingVacuously()
    {
        var clean = BuildCleaner().CleanPdf([]);

        var report = BuildValidator().Validate([], clean);

        // totalAttempted == 0 forces errorRate to 100, so an empty run never passes silently.
        Assert.IsFalse(report.Passed);
    }

    [TestMethod]
    public void ContentWithoutHeadings_NeedsFallbackChunking()
    {
        var page  = Page("doc1.pdf", "Plain text, no headings at all.");
        var clean = BuildCleaner().CleanPdf([page]);

        var report = BuildValidator().Validate([FileResult("doc1.pdf", [page])], clean);

        CollectionAssert.Contains(report.DocumentsNeedingFallbackChunking.ToList(), "doc1.pdf");
    }

    [TestMethod]
    public void MetadataDiagnosticsWarnings_AreFoldedIntoIssues_AsAdvisoryNotGating()
    {
        var page = Page("doc1.pdf", "## Heading\nSome markdown content");
        var clean = BuildCleaner().CleanPdf([page]);
        var metadataDiagnostics = new PdfStepDiagnostics(
            [
                new ExtractionWarning(RowNumber: null, DocumentId: "doc1.pdf", Message: "No native Title in the PDF's Info dictionary - falls back to a filename-derived title downstream."),
                new ExtractionWarning(RowNumber: null, DocumentId: "doc1.pdf", Message: "No native Producer in the PDF's Info dictionary — possible non-standard export pipeline."),
            ],
            []);

        var report = BuildValidator().Validate(
            [FileResult("doc1.pdf", [page], metadataDiagnostics: metadataDiagnostics)], clean);

        var metadataIssues = report.Issues.Where(i => i.Stage == "Metadata").ToList();
        Assert.AreEqual(2, metadataIssues.Count);
        Assert.IsTrue(metadataIssues.All(i => i.Severity == "Warning"));
        Assert.IsTrue(metadataIssues.All(i => i.DocumentId == "doc1.pdf"));
        Assert.IsTrue(metadataIssues.Any(i => i.Message.Contains("No native Title")));
        Assert.IsTrue(metadataIssues.Any(i => i.Message.Contains("No native Producer")));
        // Warnings never gate - the happy-path content above still passes despite these.
        Assert.IsTrue(report.Passed);
    }

    [TestMethod]
    public void ContentWithMarkdownHeading_DoesNotNeedFallbackChunking()
    {
        var page  = Page("doc1.pdf", "## Heading\nSome content under it.");
        var clean = BuildCleaner().CleanPdf([page]);

        var report = BuildValidator().Validate([FileResult("doc1.pdf", [page])], clean);

        CollectionAssert.DoesNotContain(report.DocumentsNeedingFallbackChunking.ToList(), "doc1.pdf");
    }

    [TestMethod]
    public void ReplacementCharacterInContent_IsTextQualityError()
    {
        var page  = Page("doc1.pdf", "Corrupted � text");
        var clean = BuildCleaner().CleanPdf([page]);

        var report = BuildValidator().Validate([FileResult("doc1.pdf", [page])], clean);

        Assert.IsTrue(report.Issues.Any(i => i.Stage == "TextQuality" && i.Severity == "Error"));
        Assert.IsFalse(report.Passed);
    }

    [TestMethod]
    public void RepeatedTrigram_IsFlaggedAsPossibleFlattenedTable()
    {
        // Simulates a table collapsed into run-on prose: the same 3-word phrase repeats
        // across what used to be distinct rows. 11 repeats (33 words) clears
        // MinWordsForFlatteningCheck (30) - TableFlatteningCheck skips shorter pages
        // before it ever looks at trigram repeats, regardless of how many there are.
        var content = string.Join(" ", Enumerable.Repeat("Naam Bond Waarde", 11));
        var page    = Page("doc1.pdf", content);
        var clean   = BuildCleaner().CleanPdf([page]);

        var report = BuildValidator().Validate([FileResult("doc1.pdf", [page])], clean);

        Assert.IsTrue(report.Issues.Any(i => i.Stage == "TableFlattening"));
    }

    [TestMethod]
    public void MagnitudeShiftBeyondThreshold_DoesNotFailPassed_ButStillReportsWarning()
    {
        var (fileResults, clean) = HappyPath(); // 1 cleaned record

        // Previous run had 100 - a drop to 1 is a -99% shift, way past the 20% threshold.
        // Magnitude is advisory-only (see PdfPipelineValidator's tiering comment) - it must
        // never fail Passed, only show up in MagnitudeWarnings.
        var report = BuildValidator().Validate(fileResults, clean, previousRunCleanedCount: 100);

        Assert.IsTrue(report.Passed);
        Assert.AreEqual(1, report.MagnitudeWarnings.Count);
    }

    [TestMethod]
    public void MagnitudeShiftWithinThreshold_Passes()
    {
        var (fileResults, clean) = HappyPath(); // 1 cleaned record

        var report = BuildValidator().Validate(fileResults, clean, previousRunCleanedCount: 1);

        Assert.IsTrue(report.Passed);
        Assert.AreEqual(0, report.MagnitudeWarnings.Count);
    }

    [TestMethod]
    public void NoPreviousRunCount_SkipsMagnitudeCheck()
    {
        var (fileResults, clean) = HappyPath();

        var report = BuildValidator().Validate(fileResults, clean, previousRunCleanedCount: null);

        Assert.AreEqual(0, report.MagnitudeWarnings.Count);
    }

    [TestMethod]
    public void ZeroCleanedRecords_FailsEvenWithNoOtherErrors()
    {
        var clean = new PdfCleanResult();

        var report = BuildValidator().Validate([], clean);

        Assert.IsFalse(report.Passed);
        Assert.IsTrue(report.ReconciliationProblems.Any(p => p.Contains("Zero cleaned records")));
    }

    [TestMethod]
    public void DetectedTableCount_SumsRealTableDataAcrossFiles()
    {
        var page  = Page("doc1.pdf", "Some content");
        var clean = BuildCleaner().CleanPdf([page]);
        var structure = Structure(
            new TableInfo(2, 3, [new TableCellInfo(0, 0, "content", "a", null, null)], Offset: 0, PageNumber: 1, Caption: null, Footnotes: [], Regions: []),
            new TableInfo(1, 1, [new TableCellInfo(0, 0, "content", "b", null, null)], Offset: 10, PageNumber: 1, Caption: null, Footnotes: [], Regions: []));

        var report = BuildValidator().Validate([FileResult("doc1.pdf", [page], structure)], clean);

        Assert.AreEqual(2, report.DetectedTableCount);
    }

    [TestMethod]
    public void MalformedTable_ZeroRowsOrColumns_IsFlaggedAsWarning()
    {
        var page  = Page("doc1.pdf", "Some content");
        var clean = BuildCleaner().CleanPdf([page]);
        var structure = Structure(new TableInfo(0, 0, [], Offset: 0, PageNumber: 1, Caption: null, Footnotes: [], Regions: []));

        var report = BuildValidator().Validate([FileResult("doc1.pdf", [page], structure)], clean);

        Assert.IsTrue(report.Issues.Any(i =>
            i.Stage == "TableStructure" && i.Severity == "Warning" && i.Message.Contains("malformed")));
    }

    [TestMethod]
    public void TableWithNoCellData_IsFlaggedAsWarning()
    {
        var page  = Page("doc1.pdf", "Some content");
        var clean = BuildCleaner().CleanPdf([page]);
        var structure = Structure(new TableInfo(2, 2, [], Offset: 0, PageNumber: 1, Caption: null, Footnotes: [], Regions: []));

        var report = BuildValidator().Validate([FileResult("doc1.pdf", [page], structure)], clean);

        Assert.IsTrue(report.Issues.Any(i =>
            i.Stage == "TableStructure" && i.Severity == "Warning" && i.Message.Contains("no cell data")));
    }

    [TestMethod]
    public void NoStructuresProvided_DetectedTableCountIsZeroAndNoQualityIssues()
    {
        var page  = Page("doc1.pdf", "Some content");
        var clean = BuildCleaner().CleanPdf([page]);

        var report = BuildValidator().Validate([FileResult("doc1.pdf", [page])], clean);

        Assert.AreEqual(0, report.DetectedTableCount);
        Assert.IsFalse(report.Issues.Any(i => i.Stage == "TableStructure"));
    }

    [TestMethod]
    public void DuplicatePageFromExtractor_FailsViaReconciliation_NotErrorRate()
    {
        // Two distinct pages plus one deliberate duplicate of page 0 - modeling the
        // extractor reporting the same (BlobName, PageNumber) twice, since PdfCleaner no
        // longer dedupes at all. Neither page trips any Issue-level check, so the
        // error-rate gate alone would pass this run; only the reconciliation check
        // (unconditional, no rate threshold) should fail it - proving the
        // previously-dormant duplicate-key check actually activates now that PdfCleaner
        // isn't silently absorbing the duplicate before validation ever sees it.
        var page0    = Page("doc1.pdf", "## Heading\nPage zero content.",       pageIndex: 0);
        var page0Dup = Page("doc1.pdf", "## Heading\nPage zero content again.", pageIndex: 0);
        var page1    = Page("doc1.pdf", "## Heading\nPage one content.",        pageIndex: 1);

        var allPages    = new[] { page0, page0Dup, page1 };
        var fileResults = new[] { FileResult("doc1.pdf", allPages) };
        var clean        = BuildCleaner().CleanPdf(allPages);

        var report = BuildValidator().Validate(fileResults, clean);

        Assert.AreEqual(0, report.Issues.Count(i => i.Severity == "Error")); // error-rate alone would pass
        Assert.IsTrue(report.ReconciliationProblems.Count > 0);
        Assert.IsFalse(report.Passed);
    }
}
