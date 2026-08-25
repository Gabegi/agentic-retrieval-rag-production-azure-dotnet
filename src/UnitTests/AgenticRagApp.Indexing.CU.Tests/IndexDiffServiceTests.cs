using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Services;

namespace RagApp.UnitTests.CsvExtraction;

// Direct unit tests for the diff decision, at the level it actually makes it:
// CompareSourceListingToIndex over two in-memory listings, with no blob store, no index client
// and no orchestrator in the way.
//
// ExtractionServiceTests drives the same logic end-to-end through a real IndexDiffService (see
// its BuildService) - that pair is deliberate: these tests pin the rules, those tests pin that
// ExtractionService still wires them up and consumes the result correctly.
[TestClass]
public class IndexDiffServiceTests
{
    private static readonly DateTimeOffset Old = DateTimeOffset.Parse("2024-01-01T00:00:00Z");
    private static readonly DateTimeOffset New = DateTimeOffset.Parse("2024-06-01T00:00:00Z");

    private static PdfBlobInfo Entry(DateTimeOffset lastModified, ZenyaMetadata? zenya = null) =>
        new(lastModified, ContentLength: null, zenya ?? ZenyaMetadata.Empty);

    private static Dictionary<string, PdfBlobInfo> Source(params (string Id, PdfBlobInfo Entry)[] entries) =>
        entries.ToDictionary(e => e.Id, e => e.Entry, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, DateTimeOffset> Indexed(params (string Id, DateTimeOffset When)[] entries) =>
        entries.ToDictionary(e => e.Id, e => e.When, StringComparer.OrdinalIgnoreCase);

    [TestMethod]
    public void NotYetIndexed_IsNew_AndProcessed()
    {
        var result = IndexDiffService.CompareSourceListingToIndex(
            Source(("doc1.pdf", Entry(Old))), Indexed(), forceReindex: false);

        Assert.AreEqual(1, result.NewCount);
        Assert.AreEqual(0, result.Updated);
        CollectionAssert.AreEquivalent(new[] { "doc1.pdf" }, result.SourceIdsToProcess.ToList());
        // Nothing to tear down: there are no existing chunks for a document the index has
        // never seen.
        Assert.AreEqual(0, result.ToDeleteChunks.Count);
    }

    [TestMethod]
    public void IndexedAndUnchanged_IsSkipped_AndNeverCostsAnExtraction()
    {
        var result = IndexDiffService.CompareSourceListingToIndex(
            Source(("doc1.pdf", Entry(Old))), Indexed(("doc1.pdf", New)), forceReindex: false);

        Assert.AreEqual(1, result.Skipped);
        Assert.AreEqual(0, result.SourceIdsToProcess.Count);
        Assert.AreEqual(0, result.ToDeleteChunks.Count);
    }

    [TestMethod]
    public void SourceNewerThanIndexed_IsUpdated_AndStagesOldChunksForDeletion()
    {
        var result = IndexDiffService.CompareSourceListingToIndex(
            Source(("doc1.pdf", Entry(New))), Indexed(("doc1.pdf", Old)), forceReindex: false);

        Assert.AreEqual(1, result.Updated);
        Assert.AreEqual(0, result.NewCount);
        CollectionAssert.AreEquivalent(new[] { "doc1.pdf" }, result.SourceIdsToProcess.ToList());
        CollectionAssert.AreEquivalent(new[] { "doc1.pdf" }, result.ToDeleteChunks);
    }

    [TestMethod]
    public void ForceReindex_ProcessesEvenUnchangedDocuments()
    {
        var result = IndexDiffService.CompareSourceListingToIndex(
            Source(("doc1.pdf", Entry(Old))), Indexed(("doc1.pdf", New)), forceReindex: true);

        Assert.AreEqual(0, result.Skipped);
        Assert.AreEqual(1, result.Updated);
        CollectionAssert.AreEquivalent(new[] { "doc1.pdf" }, result.SourceIdsToProcess.ToList());
    }

    [TestMethod]
    public void IndexedButAbsentFromSource_IsRemoved_AndTornDown()
    {
        var result = IndexDiffService.CompareSourceListingToIndex(
            Source(), Indexed(("gone.pdf", Old)), forceReindex: false);

        CollectionAssert.AreEquivalent(new[] { "gone.pdf" }, result.RemovedSourceIds);
        CollectionAssert.AreEquivalent(new[] { "gone.pdf" }, result.ToDeleteChunks);
        Assert.AreEqual(0, result.SourceIdsToProcess.Count);
    }

    [TestMethod]
    public void ZenyaInactive_IsNeverProcessed_EvenWhenNew()
    {
        var inactive = ZenyaMetadata.FromBlobMetadata(
            new Dictionary<string, string> { ["zenya_status"] = "ingetrokken" });

        var result = IndexDiffService.CompareSourceListingToIndex(
            Source(("doc1.pdf", Entry(Old, inactive))), Indexed(), forceReindex: false);

        Assert.AreEqual(1, result.Inactive);
        Assert.AreEqual(0, result.NewCount);
        Assert.AreEqual(0, result.SourceIdsToProcess.Count);
        // Not currently indexed, so there is nothing to tear down either.
        Assert.AreEqual(0, result.ToDeleteChunks.Count);
    }

    [TestMethod]
    public void ZenyaInactive_ButCurrentlyIndexed_IsTornDownLikeARemovedDocument()
    {
        var inactive = ZenyaMetadata.FromBlobMetadata(
            new Dictionary<string, string> { ["zenya_status"] = "ingetrokken" });

        var result = IndexDiffService.CompareSourceListingToIndex(
            Source(("doc1.pdf", Entry(Old, inactive))), Indexed(("doc1.pdf", Old)), forceReindex: false);

        Assert.AreEqual(1, result.Inactive);
        CollectionAssert.AreEquivalent(new[] { "doc1.pdf" }, result.RemovedSourceIds);
        CollectionAssert.AreEquivalent(new[] { "doc1.pdf" }, result.ToDeleteChunks);
        Assert.AreEqual(0, result.SourceIdsToProcess.Count);
    }

    [TestMethod]
    public void InactiveAndStillPresent_IsNotAlsoCountedAsRemovedFromBlob()
    {
        var inactive = ZenyaMetadata.FromBlobMetadata(
            new Dictionary<string, string> { ["zenya_status"] = "ingetrokken" });

        var result = IndexDiffService.CompareSourceListingToIndex(
            Source(("doc1.pdf", Entry(Old, inactive))), Indexed(("doc1.pdf", Old)), forceReindex: false);

        // Exactly one entry each - the inactive branch already staged it, and the
        // removed-from-blob sweep must not stage it a second time.
        Assert.AreEqual(1, result.RemovedSourceIds.Count);
        Assert.AreEqual(1, result.ToDeleteChunks.Count);
    }

    [TestMethod]
    public void IndexedDateMatching_IsCaseInsensitive()
    {
        // The real IndexDocumentService returns an OrdinalIgnoreCase dictionary; a document
        // whose blob name differs only in case from its indexed id must still read as
        // already-indexed rather than as new.
        var result = IndexDiffService.CompareSourceListingToIndex(
            Source(("Doc1.pdf", Entry(Old))), Indexed(("doc1.pdf", New)), forceReindex: false);

        Assert.AreEqual(0, result.NewCount);
        Assert.AreEqual(1, result.Skipped);
    }
}
