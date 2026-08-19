using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Indexing.Pdf.Services;

namespace RagApp.UnitTests.Indexing;

// DocumentSizeClassifier's own tests, kept when ChunkingStrategySelectorTests was deleted with
// the five-step selector it covered. The classifier outlived that machinery - the run report
// stamps size_class on every document row - so its boundary tests outlive it too.
//
// Threshold tests sit exactly on their boundaries so an off-by-one in a comparison operator
// cannot pass.
[TestClass]
public class DocumentSizeClassifierTests
{
    private static DocumentProfile Profile(
        bool hasContent      = true,
        int  estimatedTokens = 10_000) =>
        new(ExtractedPageCount:       10,
            TotalChars:               30_000,
            FileSizeBytes:            100_000,
            CharsPerPage:             3_000,
            BytesPerChar:             3,
            FiguresPerPage:           0,
            EstimatedTokens:          estimatedTokens,
            HasExtractableContent:    hasContent,
            DocumentIsSafeReturnUnit: null,
            NeedsNavigationSummary:   false,
            HeadingsPerThousandChars: 1.0,
            NumberedHeadingShare:     0,
            MaxSectionSizeChars:      500,
            BoilerplateShare:         0,
            SelectionMarksPerPage:    0,
            TableCharShare:           0);

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
}
