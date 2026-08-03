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

    // One file's worth of PdfExtractionResult, carrying whatever pages/structure the
    // test needs - the validator flattens this (and any other files) internally now,
    // so this is the only fixture shape tests build.
    private static PdfExtractionResult FileResult(
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
    private static (IReadOnlyList<PdfExtractionResult> FileResults, PdfCleanResult Clean) HappyPath()
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
    public void NoInputAtAll_PassesAsSteadyState()
    {
        // Regression test for finding #5: zero source documents submitted (the
        // pre-extraction diff correctly found nothing new/updated) must pass, not be
        // mistaken for a run that attempted extraction and silently got nothing back -
        // otherwise every steady-state run throws and fails the whole orchestration.
        var clean = BuildCleaner().CleanPdf([]);

        var report = BuildValidator().Validate([], clean);

        Assert.IsTrue(report.Passed);
        Assert.AreEqual(0, report.ReconciliationProblems.Count);
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
                PipelineIssue.Warning(PipelineStage.Metadata, "doc1.pdf", "No native Title in the PDF's Info dictionary - falls back to a filename-derived title downstream."),
                PipelineIssue.Warning(PipelineStage.Metadata, "doc1.pdf", "No native Producer in the PDF's Info dictionary — possible non-standard export pipeline."),
            ],
            []);

        var report = BuildValidator().Validate(
            [FileResult("doc1.pdf", [page], metadataDiagnostics: metadataDiagnostics)], clean);

        var metadataIssues = report.Issues.Where(i => i.Stage == PipelineStage.Metadata).ToList();
        Assert.AreEqual(2, metadataIssues.Count);
        Assert.IsTrue(metadataIssues.All(i => i.IsWarning));
        Assert.IsTrue(metadataIssues.All(i => i.DocumentId == "doc1.pdf"));
        Assert.IsTrue(metadataIssues.Any(i => i.Message.Contains("No native Title")));
        Assert.IsTrue(metadataIssues.Any(i => i.Message.Contains("No native Producer")));
        // Warnings never gate - the happy-path content above still passes despite these.
        Assert.IsTrue(report.Passed);
    }

    [TestMethod]
    public void MissingOptionalMetadataFields_AreAggregatedIntoOneRedFlag_NotIndividualIssues()
    {
        // Regression test for finding #15: Author/Creator/Subject/Keywords absence has no
        // downstream consequence and is reported via diag.Info, not diag.Warn - it must
        // not appear in report.Issues at all, only as one aggregate RedFlags line.
        var page = Page("doc1.pdf", "## Heading\nSome markdown content");
        var clean = BuildCleaner().CleanPdf([page]);
        var metadataDiagnostics = new PdfStepDiagnostics(
            [],
            [],
            Info:
            [
                PipelineIssue.Warning(PipelineStage.Metadata, "doc1.pdf", "No native Author in the PDF's Info dictionary."),
                PipelineIssue.Warning(PipelineStage.Metadata, "doc1.pdf", "No native Creator in the PDF's Info dictionary."),
            ]);

        var report = BuildValidator().Validate(
            [FileResult("doc1.pdf", [page], metadataDiagnostics: metadataDiagnostics)], clean);

        Assert.AreEqual(0, report.Issues.Count(i => i.Stage == PipelineStage.Metadata));
        Assert.IsTrue(report.RedFlags.Any(f => f.Contains("1 document(s)") && f.Contains("Author/Creator/Subject/Keywords")));
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
    public void ErrorSeverityIssues_AreSortedAheadOfWarnings_RegardlessOfAssemblyOrder()
    {
        // Regression test for finding #9: Metadata warnings are assembled before TextQuality
        // errors (stage order in Validate), but both PdfExtractionPipeline consumers of
        // report.Issues just take a flat prefix - without sorting, enough metadata warnings
        // can push the one severity that actually gates the run out of that prefix entirely.
        var page = Page("doc1.pdf", "Corrupted � text");
        var clean = BuildCleaner().CleanPdf([page]);
        var metadataDiagnostics = new PdfStepDiagnostics(
            [
                PipelineIssue.Warning(PipelineStage.Metadata, "doc1.pdf", "No native Title in the PDF's Info dictionary."),
                PipelineIssue.Warning(PipelineStage.Metadata, "doc1.pdf", "No native Author in the PDF's Info dictionary."),
                PipelineIssue.Warning(PipelineStage.Metadata, "doc1.pdf", "No native Producer in the PDF's Info dictionary."),
            ],
            []);

        var report = BuildValidator().Validate(
            [FileResult("doc1.pdf", [page], metadataDiagnostics: metadataDiagnostics)], clean);

        Assert.AreEqual(4, report.Issues.Count);
        Assert.AreEqual(IssueSeverity.Error, report.Issues[0].Severity);
        Assert.AreEqual(PipelineStage.TextQuality, report.Issues[0].Stage);
        Assert.IsTrue(report.Issues.Skip(1).All(i => i.IsWarning));
    }

    [TestMethod]
    public void ReplacementCharacterInContent_IsTextQualityError()
    {
        var page  = Page("doc1.pdf", "Corrupted � text");
        var clean = BuildCleaner().CleanPdf([page]);

        var report = BuildValidator().Validate([FileResult("doc1.pdf", [page])], clean);

        Assert.IsTrue(report.Issues.Any(i => i.Stage == PipelineStage.TextQuality && i.IsError));
        Assert.IsFalse(report.Passed);
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
    public void ZeroCleanedRecords_WithPagesActuallyAttempted_StillFails()
    {
        // Distinct from NoInputAtAll_PassesAsSteadyState: here extraction was actually
        // attempted (both files failed) but produced zero cleaned records - the case
        // "zero cleaned records" is meant to catch, since a pass here would let the
        // downstream diff step delete the whole index.
        var fileResults = new PdfExtractionResult[]
        {
            new(Ok: false, BlobName: "doc1.pdf", FileSizeBytes: 1024, PdfSpecVersion: null,
                NativeMetadata: null, RawContent: null, Pages: null, Structure: null,
                EstimatedCostUsd: null, Error: PipelineIssue.Error(PipelineStage.ParsePages, "doc1.pdf", "boom")),
            new(Ok: false, BlobName: "doc2.pdf", FileSizeBytes: 1024, PdfSpecVersion: null,
                NativeMetadata: null, RawContent: null, Pages: null, Structure: null,
                EstimatedCostUsd: null, Error: PipelineIssue.Error(PipelineStage.ParsePages, "doc2.pdf", "boom")),
        };
        var clean = new PdfCleanResult();

        var report = BuildValidator().Validate(fileResults, clean);

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
            i.Stage == PipelineStage.TableStructure && i.IsWarning && i.Message.Contains("malformed")));
    }

    [TestMethod]
    public void TableWithNoCellData_IsFlaggedAsWarning()
    {
        var page  = Page("doc1.pdf", "Some content");
        var clean = BuildCleaner().CleanPdf([page]);
        var structure = Structure(new TableInfo(2, 2, [], Offset: 0, PageNumber: 1, Caption: null, Footnotes: [], Regions: []));

        var report = BuildValidator().Validate([FileResult("doc1.pdf", [page], structure)], clean);

        Assert.IsTrue(report.Issues.Any(i =>
            i.Stage == PipelineStage.TableStructure && i.IsWarning && i.Message.Contains("no cell data")));
    }

    [TestMethod]
    public void NoStructuresProvided_DetectedTableCountIsZeroAndNoQualityIssues()
    {
        var page  = Page("doc1.pdf", "Some content");
        var clean = BuildCleaner().CleanPdf([page]);

        var report = BuildValidator().Validate([FileResult("doc1.pdf", [page])], clean);

        Assert.AreEqual(0, report.DetectedTableCount);
        Assert.IsFalse(report.Issues.Any(i => i.Stage == PipelineStage.TableStructure));
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

        Assert.AreEqual(0, report.Issues.Count(i => i.IsError)); // error-rate alone would pass
        Assert.IsTrue(report.ReconciliationProblems.Count > 0);
        Assert.IsFalse(report.Passed);
    }

    // --- BuildRandomCheckSample -------------------------------------------------------

    private static CleanedPdfPageRecord Cleaned(string blobName, int pageNumber) => new()
    {
        BlobName = blobName, PageNumber = pageNumber, PageContent = "content", Title = "Title",
    };

    private static PdfCleanResult CleanResultWith(int count)
    {
        var result = new PdfCleanResult();
        for (var i = 0; i < count; i++)
            result.AddRecord(Cleaned($"doc{i}.pdf", 0));
        return result;
    }

    [TestMethod]
    public void RecordCountAtOrBelowSampleSize_ReturnsEveryRecord()
    {
        var clean = CleanResultWith(5); // == SpotCheckSampleSize

        var sample = PdfPipelineValidator.BuildRandomCheckSample(clean, seed: 1);

        Assert.AreEqual(5, sample.Count);
        CollectionAssert.AreEquivalent(clean.Records.ToList(), sample);
    }

    [TestMethod]
    public void FewerRecordsThanSampleSize_ReturnsAllOfThem()
    {
        var clean = CleanResultWith(2);

        var sample = PdfPipelineValidator.BuildRandomCheckSample(clean, seed: 1);

        Assert.AreEqual(2, sample.Count);
    }

    [TestMethod]
    public void MoreRecordsThanSampleSize_ReturnsExactlySampleSizeRecords()
    {
        var clean = CleanResultWith(50);

        var sample = PdfPipelineValidator.BuildRandomCheckSample(clean, seed: 1);

        Assert.AreEqual(5, sample.Count);
        // Every sampled record must actually come from the source set (reservoir sampling
        // invariant), with no duplicates.
        Assert.IsTrue(sample.All(s => clean.Records.Contains(s)));
        Assert.AreEqual(sample.Count, sample.Distinct().Count());
    }

    [TestMethod]
    public void SameSeed_ProducesTheSameSampleAcrossCalls()
    {
        var clean = CleanResultWith(50);

        var sampleA = PdfPipelineValidator.BuildRandomCheckSample(clean, seed: 42);
        var sampleB = PdfPipelineValidator.BuildRandomCheckSample(clean, seed: 42);

        CollectionAssert.AreEqual(sampleA, sampleB);
    }

    [TestMethod]
    public void NoExplicitSeed_IsStillDeterministicForTheSameInput()
    {
        // seed: null derives a stable FNV-1a hash of the page keys (StableSeed) rather than
        // a random one, so the same input set produces the same sample run over run -
        // important for validation-report.json to stay diffable.
        var cleanA = CleanResultWith(50);
        var cleanB = CleanResultWith(50);

        var sampleA = PdfPipelineValidator.BuildRandomCheckSample(cleanA, seed: null);
        var sampleB = PdfPipelineValidator.BuildRandomCheckSample(cleanB, seed: null);

        CollectionAssert.AreEqual(
            sampleA.Select(r => (r.BlobName, r.PageNumber)).ToList(),
            sampleB.Select(r => (r.BlobName, r.PageNumber)).ToList());
    }

    [TestMethod]
    public void DifferentSeeds_CanProduceDifferentSamples()
    {
        var clean = CleanResultWith(50);

        var sampleA = PdfPipelineValidator.BuildRandomCheckSample(clean, seed: 1);
        var sampleB = PdfPipelineValidator.BuildRandomCheckSample(clean, seed: 2);

        CollectionAssert.AreNotEqual(sampleA, sampleB);
    }

    [TestMethod]
    public void EmptyCleanResult_ReturnsEmptySample()
    {
        var clean = new PdfCleanResult();

        var sample = PdfPipelineValidator.BuildRandomCheckSample(clean, seed: 1);

        Assert.AreEqual(0, sample.Count);
    }
}
