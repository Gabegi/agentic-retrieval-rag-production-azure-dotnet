using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Indexing.Pdf.Services;

namespace RagApp.UnitTests.Indexing;

// The five selection steps, each on its own inputs, plus one end-to-end DetermineStrategy.
// Threshold tests sit exactly on their boundaries so an off-by-one in a comparison operator
// cannot pass.
[TestClass]
public class ChunkingStrategySelectorTests
{
    private static DocumentProfile Profile(
        bool   hasContent               = true,
        int    estimatedTokens          = 10_000,
        bool?  safeReturnUnit           = null,
        int    maxSectionSizeChars      = 500,
        // Corpus-typical density (~1 heading per 1,000 chars); tests that probe the
        // SectionChecker floor pass their own value.
        double headingsPerThousandChars = 1.0,
        double tableCharShare           = 0) =>
        new(ExtractedPageCount:       10,
            TotalChars:               30_000,
            FileSizeBytes:            100_000,
            CharsPerPage:             3_000,
            BytesPerChar:             3,
            FiguresPerPage:           0,
            EstimatedTokens:          estimatedTokens,
            HasExtractableContent:    hasContent,
            DocumentIsSafeReturnUnit: safeReturnUnit,
            NeedsNavigationSummary:   false,
            HeadingsPerThousandChars: headingsPerThousandChars,
            NumberedHeadingShare:     0,
            MaxSectionSizeChars:      maxSectionSizeChars,
            BoilerplateShare:         0,
            SelectionMarksPerPage:    0,
            TableCharShare:           tableCharShare);

    // ── Step 1: DocumentSizeClassifier ──────────────────────────────────────

    [TestMethod]
    public void SizeClass_AtLargeThreshold_IsLarge() =>
        Assert.AreEqual(DocumentSizeClass.Large,
            DocumentSizeClassifier.Classify(Profile(estimatedTokens: 50_000)));

    [TestMethod]
    public void SizeClass_JustBelowLargeThreshold_IsMedium() =>
        Assert.AreEqual(DocumentSizeClass.Medium,
            DocumentSizeClassifier.Classify(Profile(estimatedTokens: 49_999)));

    [TestMethod]
    public void SizeClass_AtMediumThreshold_IsMedium() =>
        Assert.AreEqual(DocumentSizeClass.Medium,
            DocumentSizeClassifier.Classify(Profile(estimatedTokens: 4_000)));

    [TestMethod]
    public void SizeClass_JustBelowMediumThreshold_IsSmall() =>
        Assert.AreEqual(DocumentSizeClass.Small,
            DocumentSizeClassifier.Classify(Profile(estimatedTokens: 3_999)));

    [TestMethod]
    public void SizeClass_NoExtractableContent_IsPicture_RegardlessOfTokens() =>
        Assert.AreEqual(DocumentSizeClass.Picture,
            DocumentSizeClassifier.Classify(Profile(hasContent: false, estimatedTokens: 90_000)));

    [TestMethod]
    public void SizeClass_NullProfile_DefaultsToMedium() =>
        Assert.AreEqual(DocumentSizeClass.Medium, DocumentSizeClassifier.Classify(null));

    // ── Step 2: ParentGrainChecker ──────────────────────────────────────────
    // Size class only - the measured DocumentIsSafeReturnUnit flag is deliberately not
    // consulted until Phase D produces it (see ParentGrainChecker).

    [TestMethod]
    public void ParentGrain_Small_IsWholeDocument() =>
        Assert.AreEqual(ParentGrain.WholeDocument,
            ParentGrainChecker.Determine(DocumentSizeClass.Small));

    [TestMethod]
    public void ParentGrain_Medium_IsParentChild() =>
        Assert.AreEqual(ParentGrain.ParentChild,
            ParentGrainChecker.Determine(DocumentSizeClass.Medium));

    [TestMethod]
    public void ParentGrain_Picture_IsParentChild() =>
        Assert.AreEqual(ParentGrain.ParentChild,
            ParentGrainChecker.Determine(DocumentSizeClass.Picture));

    // ── Step 3: SectionChecker ──────────────────────────────────────────────
    // Count AND density: a bare count readmits "large but unstructured" (2 headings on
    // 400 pages), so both boundaries are pinned here.

    [TestMethod]
    public void Sections_TwoHeadingsAtNormalDensity_AreUsable() =>
        Assert.IsTrue(SectionChecker.HasUsableSections(2, Profile()));

    [TestMethod]
    public void Sections_OneHeading_IsNotUsable_RegardlessOfDensity() =>
        Assert.IsFalse(SectionChecker.HasUsableSections(1, Profile(headingsPerThousandChars: 5.0)));

    [TestMethod]
    public void Sections_DensityAtFloor_IsUsable() =>
        Assert.IsTrue(SectionChecker.HasUsableSections(
            2, Profile(headingsPerThousandChars: SectionChecker.MinHeadingsPerThousandChars)));

    [TestMethod]
    public void Sections_SparseHeadingGiant_IsNotUsable() =>
        // The 400-page/2-heading shape: ~0.0025 headings per 1,000 chars.
        Assert.IsFalse(SectionChecker.HasUsableSections(2, Profile(headingsPerThousandChars: 0.0025)));

    [TestMethod]
    public void Sections_NullProfile_CountAloneDecides() =>
        Assert.IsTrue(SectionChecker.HasUsableSections(2, null));

    // ── Step 4: TableChecker ────────────────────────────────────────────────
    // Dominance, not count: at least half the document's characters live in table blocks.

    [TestMethod]
    public void Tables_ShareAtHalf_IsTableShaped() =>
        Assert.IsTrue(TableChecker.IsTableShaped(1, Profile(tableCharShare: TableChecker.MinTableCharShare)));

    [TestMethod]
    public void Tables_ShareJustBelowHalf_IsNotTableShaped() =>
        Assert.IsFalse(TableChecker.IsTableShaped(50, Profile(tableCharShare: 0.49)));

    [TestMethod]
    public void Tables_ManyTablesInAProseOcean_IsNotTableShaped() =>
        // The 10,000-page counterexample: a high count means nothing when tables are a
        // sliver of the characters.
        Assert.IsFalse(TableChecker.IsTableShaped(3, Profile(tableCharShare: 0.001)));

    [TestMethod]
    public void Tables_NullProfile_FallsBackToCount()
    {
        Assert.IsTrue(TableChecker.IsTableShaped(3, null));
        Assert.IsFalse(TableChecker.IsTableShaped(2, null));
    }

    // ── Step 5: ChunkingStrategyPicker ──────────────────────────────────────
    // Every branch is earned; Fallback is the default.

    [TestMethod]
    public void Picker_Picture_IsFallback_EvenWithSectionsAndTables() =>
        Assert.AreEqual(ChunkingStrategyKind.Fallback,
            ChunkingStrategyPicker.Pick(DocumentSizeClass.Picture, hasUsableSections: true, isTableShaped: true, headingCount: 50));

    [TestMethod]
    public void Picker_UsableSections_BeatTables() =>
        Assert.AreEqual(ChunkingStrategyKind.HeadingBased,
            ChunkingStrategyPicker.Pick(DocumentSizeClass.Large, hasUsableSections: true, isTableShaped: true, headingCount: 200));

    [TestMethod]
    public void Picker_TableShaped_IsTableAware() =>
        Assert.AreEqual(ChunkingStrategyKind.TableAware,
            ChunkingStrategyPicker.Pick(DocumentSizeClass.Small, hasUsableSections: false, isTableShaped: true, headingCount: 1));

    [TestMethod]
    public void Picker_SmallWithAHeading_EarnsSingleSection() =>
        Assert.AreEqual(ChunkingStrategyKind.SingleSection,
            ChunkingStrategyPicker.Pick(DocumentSizeClass.Small, hasUsableSections: false, isTableShaped: false, headingCount: 1));

    [TestMethod]
    public void Picker_SmallWithoutAnyHeading_FallsBack() =>
        Assert.AreEqual(ChunkingStrategyKind.Fallback,
            ChunkingStrategyPicker.Pick(DocumentSizeClass.Small, hasUsableSections: false, isTableShaped: false, headingCount: 0));

    [TestMethod]
    public void Picker_LargeWithoutStructure_FallsBack() =>
        // The "large but unstructured" case must surface as Fallback, not hide under a
        // benign SingleSection label.
        Assert.AreEqual(ChunkingStrategyKind.Fallback,
            ChunkingStrategyPicker.Pick(DocumentSizeClass.Large, hasUsableSections: false, isTableShaped: false, headingCount: 1));

    // ── End to end: DetermineStrategy ───────────────────────────────────────

    [TestMethod]
    public void DetermineStrategy_UnprofiledUnstructuredDoc_TakesTheSafeDefaults()
    {
        var selector = new ChunkingStrategySelector();

        var decision = selector.DetermineStrategy(Doc(profile: null));

        Assert.AreEqual(DocumentSizeClass.Medium,      decision.SizeClass);
        Assert.AreEqual(ParentGrain.ParentChild,       decision.ParentGrain);
        Assert.IsFalse(decision.HasUsableSections);
        Assert.AreEqual(0,                             decision.HeadingCount);
        // Medium (not Small) with no structure earns nothing - the default is Fallback.
        Assert.AreEqual(ChunkingStrategyKind.Fallback, decision.Strategy);
    }

    [TestMethod]
    public void DetermineStrategy_HeadedDoc_IsHeadingBased()
    {
        var selector = new ChunkingStrategySelector();
        var headings = new[]
        {
            new Heading("Intro",   "sectionHeading", 0,  1),
            new Heading("Details", "sectionHeading", 50, 1),
        };

        var decision = selector.DetermineStrategy(Doc(profile: Profile(), headings: headings));

        Assert.AreEqual(ChunkingStrategyKind.HeadingBased, decision.Strategy);
        Assert.AreEqual(2, decision.HeadingCount);
    }

    private static PdfExtractionDocument Doc(
        DocumentProfile? profile, IReadOnlyList<Heading>? headings = null) =>
        new(SourceId:         "doc1",
            Content:          "body",
            PageSpans:        [],
            Title:            "Title",
            Author:           null,
            CreatedAt:        null,
            ModDate:          null,
            PageCount:        null,
            LastModifiedDate: null,
            ZenyaDocumentId:  null,
            ZenyaVersion:     null,
            ZenyaStatus:      null,
            ZenyaUrl:         null,
            Bookmarks:        [],
            PageBreadcrumbs:  new Dictionary<int, string>(),
            Sections:         [],
            Headings:         headings ?? [],
            Boilerplate:      [],
            Tables:           [],
            SelectionMarks:   [],
            Figures:          [],
            Lines:            [],
            Profile:          profile,
            Language:         null);
}
