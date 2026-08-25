using AgenticRagApp.Common.Models;
using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Services;

namespace RagApp.UnitTests.PdfExtraction;

// Assembly and output shape. The paid call itself is covered by
// ContentUnderstandingAnalyzerTests; what matters here is what happens to the results once a
// corpus of them exists - above all that the order is stable, because chunk ids are derived
// from a document's position in this list.
[TestClass]
public class ExtractionOutputBuilderTests
{
    private static ExtractedFile Ok(string blobName, int pages = 1) =>
        new(true, blobName,
            Content:   $"Inhoud van {blobName}",
            PageSpans: [.. Enumerable.Range(1, pages).Select(p => new PageSpan(p, 0, 5, null, false))],
            Structure: new PdfDocumentStructure([], [], [], [], [], [], [], []),
            Title:     blobName.Replace(".pdf", ""),
            Profile:   null,
            Language:  "nl",
            Usage:     null,
            Error:     null,
            Warnings:  []);

    private static ExtractedFile Failed(string blobName) =>
        ExtractedFile.Failed(
            blobName,
            PipelineIssue.Error(PipelineStage.ParsePages, blobName, "kapot", reason: PdfOpenFailureReason.Unknown));

    // The per-blob facts the run was handed. Empty is the normal case in these tests: absent
    // means "no listing entry for this blob", which every field below has to survive.
    private static Dictionary<string, PdfBlobInfo> NoEntries() => new(StringComparer.OrdinalIgnoreCase);

    [TestMethod]
    public void BuildDocuments_OrdersByBlobName_SoChunkIdsAreStableAcrossRuns()
    {
        // The extraction loop is parallel and collects into a bag, so its order is
        // nondeterministic. Chunk ids are built from SourceId plus the chunk's index within the
        // document list, so an unstable order here reshuffles every chunk id from one run to the
        // next - which reads downstream as the whole corpus having changed.
        var files = new[] { Ok("zebra.pdf"), Ok("alpha.pdf"), Ok("midden.pdf") };

        var documents = ExtractionOutputBuilder.BuildDocuments(files, NoEntries());

        CollectionAssert.AreEqual(
            new[] { "alpha.pdf", "midden.pdf", "zebra.pdf" },
            documents.Select(d => d.SourceId).ToList());
    }

    [TestMethod]
    public void BuildDocuments_OrdersCaseSensitively()
    {
        // Azure blob names are case-sensitive: "Beleid.pdf" and "beleid.pdf" are two different
        // blobs that can legally coexist in one container. Ordinal ordering keeps them apart and
        // stable; an ignore-case comparer would tie-break on bag order and drift.
        var files = new[] { Ok("beleid.pdf"), Ok("Beleid.pdf") };

        var documents = ExtractionOutputBuilder.BuildDocuments(files, NoEntries());

        Assert.AreEqual(2, documents.Count);
        CollectionAssert.AreEqual(
            new[] { "Beleid.pdf", "beleid.pdf" },
            documents.Select(d => d.SourceId).ToList());
    }

    [TestMethod]
    public void BuildDocuments_SkipsFailedFiles()
    {
        var files = new[] { Ok("goed.pdf"), Failed("kapot.pdf") };

        var documents = ExtractionOutputBuilder.BuildDocuments(files, NoEntries());

        Assert.AreEqual(1, documents.Count);
        Assert.AreEqual("goed.pdf", documents[0].SourceId);
    }

    [TestMethod]
    public void BuildDocuments_ReportsExtractedPageCount_NotNativeMetadata()
    {
        // There is no native PDF metadata any more - the page count a document reports is the
        // number of pages actually extracted, which is the honest number and the one the profile
        // and every report already mean.
        var documents = ExtractionOutputBuilder.BuildDocuments([Ok("doc.pdf", pages: 7)], NoEntries());

        Assert.AreEqual(7, documents[0].PageCount);
    }

    [TestMethod]
    public void BuildDocuments_CarriesZenyaMetadataAndLastModified()
    {
        // Both come straight off the pre-extraction listing entry - the loop no longer copies
        // them into side dictionaries on the way through.
        var when  = DateTimeOffset.Parse("2026-08-24T10:00:00Z");
        var zenya = ZenyaMetadata.FromBlobMetadata(new Dictionary<string, string>
        {
            ["zenya_document_id"] = "D-123",
            ["zenya_version"]     = "3",
        });

        var documents = ExtractionOutputBuilder.BuildDocuments(
            [Ok("doc.pdf")],
            new Dictionary<string, PdfBlobInfo>(StringComparer.OrdinalIgnoreCase)
            {
                ["doc.pdf"] = new PdfBlobInfo(when, ContentLength: 1234, zenya),
            });

        Assert.AreEqual(when,  documents[0].LastModifiedDate);
        Assert.AreEqual("D-123", documents[0].ZenyaDocumentId);
        Assert.AreEqual("3",     documents[0].ZenyaVersion);
    }

    [TestMethod]
    public void BuildDocuments_SurvivesAnExtractedFileWithNoListingEntry()
    {
        // Nothing produces this today - the loop only extracts blobs it was handed - but the
        // builder is a pure function over two inputs that nothing forces to agree, and an absent
        // entry must read as "no facts", not throw.
        var documents = ExtractionOutputBuilder.BuildDocuments([Ok("wees.pdf")], NoEntries());

        Assert.AreEqual(1, documents.Count);
        Assert.IsNull(documents[0].LastModifiedDate);
        Assert.IsNull(documents[0].ZenyaDocumentId);
    }

    [TestMethod]
    public void BuildExtractionOutput_CountsErrorsAndWarningsSeparately()
    {
        var files = new[] { Ok("goed.pdf"), Failed("kapot.pdf") };

        var output = ExtractionOutputBuilder.BuildExtractionOutput(files, NoEntries());

        Assert.AreEqual(1, output.ValidationErrors);
        Assert.AreEqual(0, output.ValidationWarnings);
        Assert.AreEqual(1, output.Issues.Count);
    }

    [TestMethod]
    public void BuildExtractionOutput_FlagsDocumentsWithNoZenyaDocumentId()
    {
        // Citations built from these show a traceability gap, so the run report says so rather
        // than leaving it to be noticed in the UI.
        var output = ExtractionOutputBuilder.BuildExtractionOutput([Ok("doc.pdf")], NoEntries());

        Assert.AreEqual(1, output.TraceabilityGapCount);
        Assert.IsTrue(output.RedFlags.Any(f => f.Contains("zenya_document_id")));
    }

    [TestMethod]
    public void BuildExtractionOutput_ReportsNullUsage_WhenNothingReportedAny()
    {
        // Null and zero mean different things: "no usage was readable" is a reporting gap worth
        // seeing, "zero billed" is a real and unremarkable outcome.
        var output = ExtractionOutputBuilder.BuildExtractionOutput([Ok("doc.pdf")], NoEntries());

        Assert.IsNull(output.BilledPagesStandard);
        Assert.IsNull(output.BilledContextualizationTokens);
    }

    [TestMethod]
    public void BuildExtractionOutput_FieldsWithNoEquivalentConceptStayNull()
    {
        // Null = "this source has no such concept" and must stay distinguishable from a real
        // zero, which is what the report schema documents these as.
        var output = ExtractionOutputBuilder.BuildExtractionOutput([Ok("doc.pdf")], NoEntries());

        Assert.IsNull(output.StaleDocCount);
        Assert.IsNull(output.MissingDepartmentCount);
    }

    // ── Content hashes ───────────────────────────────────────────────────────
    // The hash is measurement, not pipeline input, so what is worth pinning is that the
    // measurement reaches the places that report it - and that it counts the right files.

    [TestMethod]
    public void BuildContentHashes_IncludesFilesThatHashedThenFailedToAnalyze()
    {
        // The hash is taken before anything is submitted, so a file that downloaded and then
        // failed analysis still has one. Dropping it here would undercount the corpus and hide a
        // duplicate whose twin happens to be the failing copy.
        var files = new[]
        {
            Ok("goed.pdf")      with { ContentHash = "AAAA" },
            Failed("kapot.pdf") with { ContentHash = "BBBB" },
        };

        var hashes = ExtractionOutputBuilder.BuildContentHashes(files);

        Assert.AreEqual(2, hashes.Count);
        Assert.IsTrue(hashes.Single(h => h.BlobName == "goed.pdf").Ok);
        Assert.IsFalse(hashes.Single(h => h.BlobName == "kapot.pdf").Ok);
    }

    [TestMethod]
    public void BuildContentHashes_SkipsFilesThatNeverProducedBytes()
    {
        // No hash = the download itself produced nothing. Absent rather than counted, so it
        // cannot read as an extra distinct document in the distinct-vs-total evidence.
        var files = new[] { Ok("goed.pdf") with { ContentHash = "AAAA" }, Failed("weg.pdf") };

        var hashes = ExtractionOutputBuilder.BuildContentHashes(files);

        Assert.AreEqual(1, hashes.Count);
        Assert.AreEqual("goed.pdf", hashes[0].BlobName);
    }

    [TestMethod]
    public void BuildContentHashes_OrdersByBlobName_SoTwoRunsDiffCleanly()
    {
        var files = new[]
        {
            Ok("zebra.pdf")  with { ContentHash = "CCCC" },
            Ok("alpha.pdf")  with { ContentHash = "AAAA" },
            Ok("midden.pdf") with { ContentHash = "BBBB" },
        };

        var hashes = ExtractionOutputBuilder.BuildContentHashes(files);

        CollectionAssert.AreEqual(
            new[] { "alpha.pdf", "midden.pdf", "zebra.pdf" },
            hashes.Select(h => h.BlobName).ToList());
    }

    [TestMethod]
    public void BuildExtractionOutput_ByteIdenticalDocumentsRaiseARedFlag()
    {
        // Two blobs, same bytes = the same file uploaded twice. Raised as a red flag because that
        // is the channel that reaches the run report and the run email; a log line alone would
        // only reach whoever went looking.
        var files = new[]
        {
            Ok("beleid.pdf")      with { ContentHash = "AAAA" },
            Ok("beleid-kopie.pdf") with { ContentHash = "AAAA" },
            Ok("ander.pdf")       with { ContentHash = "BBBB" },
        };

        var output = ExtractionOutputBuilder.BuildExtractionOutput(files, NoEntries());

        var flag = output.RedFlags.Single(f => f.StartsWith("byte_identical_duplicates:"));
        StringAssert.Contains(flag, "beleid.pdf");
        StringAssert.Contains(flag, "beleid-kopie.pdf");
        Assert.IsFalse(flag.Contains("ander.pdf"), "the non-duplicate should not be named");
    }

    [TestMethod]
    public void BuildExtractionOutput_DistinctDocumentsRaiseNoDuplicateRedFlag()
    {
        var files = new[]
        {
            Ok("een.pdf")  with { ContentHash = "AAAA" },
            Ok("twee.pdf") with { ContentHash = "BBBB" },
        };

        var output = ExtractionOutputBuilder.BuildExtractionOutput(files, NoEntries());

        Assert.IsFalse(output.RedFlags.Any(f => f.StartsWith("byte_identical_duplicates:")));
        Assert.AreEqual(2, output.ContentHashes.Count);
    }
}
