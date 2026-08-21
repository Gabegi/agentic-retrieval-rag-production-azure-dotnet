using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Infrastructure.Clients.DocumentIdentity;
using AgenticRagApp.Infrastructure.Clients.Embedding;
using AgenticRagApp.Infrastructure.Configuration;
using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Services;
using AgenticRagApp.Indexing.CU.Utils;
using AgenticRagApp.Common.Models;
using AgenticRagApp.Observability.Reports;

namespace RagApp.UnitTests.Indexing;

// ChunkingService no longer wraps a flat Chunk(string) strategy, so these run the real
// section cascade end to end rather than a mocked splitter. That is deliberate: the thing
// worth testing here is how a cut becomes an indexed row (ids, prefix, page attribution,
// parent text), and a mock that returns "one chunk per document" cannot exercise any of it.
[TestClass]
public class ChunkingServiceTests
{
    // No persisted identity; the embedding call echoes back one arbitrary vector per input.
    // ChunkingService's own behaviour is what these exercise, not DocumentIdentityResolver's
    // clustering (see DocumentIdentityResolverTests for that).
    private static DocumentIdentityResolver BuildDocumentIdentityResolver()
    {
        var embeddingClient = new Mock<IEmbeddingClient>();
        embeddingClient
            .Setup(c => c.EmbedWithRetryAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyList<string>, CancellationToken>((texts, _) =>
                Task.FromResult((texts.Select(_ => new float[] { 1f, 0f, 0f }).ToArray(), 0)));

        var store = new Mock<IDocumentIdentityStore>();
        store.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        // Dimensions match the 3-float vectors above - DocumentIdentityResolver rejects a vector whose
        // length is not the configured dimension count.
        return new DocumentIdentityResolver(
            embeddingClient.Object, store.Object,
            new IndexerConfig { OpenAiEmbeddingDimensions = 3 },
            NullLogger<DocumentIdentityResolver>.Instance);
    }

    // Captures whatever ChunkingService writes, so the report-shape tests can assert on it
    // without a blob store. Null resolver means "use the default", so a test can substitute
    // one that throws.
    private static (ChunkingService Service, List<ChunkingRunReport> Reports) BuildWithReports(
        int tokenCeiling = ChunkingBudget.TokenCeiling,
        DocumentIdentityResolver? resolver = null)
    {
        var reports = new List<ChunkingRunReport>();
        var writer  = new Mock<IPipelineArtifactWriter>();
        writer
            .Setup(w => w.WriteArtifactAsync(It.IsAny<string>(), It.IsAny<ChunkingRunReport>(), It.IsAny<CancellationToken>()))
            .Callback<string, ChunkingRunReport, CancellationToken>((_, r, _) => reports.Add(r))
            .Returns(Task.CompletedTask);

        var service = BuildChunkingService(
            tokenCeiling, resolver ?? BuildDocumentIdentityResolver(), writer.Object);

        return (service, reports);
    }

    private static ChunkingService BuildService(int tokenCeiling = ChunkingBudget.TokenCeiling)
    {
        return BuildChunkingService(
            tokenCeiling, BuildDocumentIdentityResolver(),
            new Mock<IPipelineArtifactWriter>().Object);
    }

    // The two routes HeadingSectionGate dispatches onto, exactly as DI wires them. Both are
    // empty skeletons until step 3 of docs/2608/260818/chunking-service-refactor.md, so
    // tokenCeiling has nothing to configure yet - it is kept because every test that sets it is
    // asserting cutting behaviour that comes back with the strategies.
    private static ChunkingService BuildChunkingService(
        int                      tokenCeiling,
        DocumentIdentityResolver resolver,
        IPipelineArtifactWriter  writer) =>
        new(new DeclaredBoundaryStrategy(),
            new RecursiveStrategy(),
            resolver,
            new ChunkMetadataBuilder(),
            // Step 5 owns the writer now - the service never touches it. Built here rather than
            // mocked so these tests exercise the real row assembly, which is what the report
            // assertions below are actually about.
            new ChunkingReporter(writer, NullLogger<ChunkingReporter>.Instance));

    private static PdfExtractionDocument Doc(
        string sourceId, string content,
        string                  title            = "",
        int                     page             = 1,
        string?                 author           = null,
        DateTimeOffset?         createdAt        = null,
        DateTimeOffset?         modDate          = null,
        int?                    pageCount        = null,
        DateTimeOffset?         lastModifiedDate = null,
        string?                 zenyaDocumentId  = null,
        string?                 zenyaVersion     = null,
        string?                 zenyaStatus      = null,
        string?                 zenyaUrl         = null,
        IReadOnlyList<Bookmark>? bookmarks       = null,
        IReadOnlyList<SectionInfo>? sections     = null,
        IReadOnlyList<Heading>? headings         = null,
        IReadOnlyList<Heading>? boilerplate      = null,
        IReadOnlyList<TableInfo>? tables         = null,
        IReadOnlyList<FigureInfo>? figures       = null,
        DocumentProfile?        profile          = null,
        string?                 language         = null) =>
        new(
            SourceId:         sourceId,
            Content:          content,
            PageSpans:        [new PageSpan(page, 0, content.Length, null, IsPictureOnly: false)],
            Title:            title,
            Author:           author,
            CreatedAt:        createdAt,
            ModDate:          modDate,
            PageCount:        pageCount,
            LastModifiedDate: lastModifiedDate,
            ZenyaDocumentId:  zenyaDocumentId,
            ZenyaVersion:     zenyaVersion,
            ZenyaStatus:      zenyaStatus,
            ZenyaUrl:         zenyaUrl,
            Bookmarks:        bookmarks ?? [],
            PageBreadcrumbs:  new Dictionary<int, string>(),
            Sections:         sections ?? [],
            Headings:         headings ?? [],
            Boilerplate:      boilerplate ?? [],
            Tables:           tables ?? [],
            SelectionMarks:   [],
            Figures:          figures ?? [],
            Lines:            [],
            Profile:          profile,
            Language:         language);

    private static Heading H(string content, int offset, int page = 1, int depth = 1) =>
        new(content, "sectionHeading", offset, page, depth);

    private static DocumentProfile Profile(bool hasContent) =>
        new(ExtractedPageCount: 1, TotalChars: 100, FileSizeBytes: 1000,
            CharsPerPage: 100, BytesPerChar: 10, FiguresPerPage: 0, EstimatedTokens: 30,
            HasExtractableContent: hasContent, DocumentIsSafeReturnUnit: null,
            NeedsNavigationSummary: false,
            HeadingsPerThousandChars: 0, NumberedHeadingShare: 0, MaxSectionSizeChars: 100,
            BoilerplateShare: 0, SelectionMarksPerPage: 0);

    // ── identity ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Name_ReportsTheTwoAxisModel()
    {
        Assert.AreEqual("TwoAxisChunking", BuildService().Name);
    }

    [TestMethod]
    public async Task Id_IsScopedToDocumentSectionAndChild_NotToThePage()
    {
        // The page number used to be in the key, so inserting one page shifted the id of every
        // chunk after it - and an id change is a delete-plus-insert in the index, not an update.
        var (docs, _, _) = await BuildService().ChunkDocumentsAsync([Doc("doc1", "content", page: 7)]);

        Assert.AreEqual(ChunkingHelper.SafeKey("doc1::s0", 0), docs[0].Id);
    }

    [TestMethod]
    public async Task SectionId_IsSynthesized_AndSharedByEveryChildOfOneSection()
    {
        // There is no parent row for section_id to point at - parent text is materialized onto
        // each child instead. It is a grouping key, so what matters is that siblings agree.
        var body = string.Join(" ", Enumerable.Repeat("woord", 400));
        var (docs, _, _) = await BuildService(tokenCeiling: 60).ChunkDocumentsAsync([Doc("doc1", body)]);

        Assert.IsTrue(docs.Count > 1, "expected the section to be split");
        Assert.AreEqual(1, docs.Select(d => d.SectionId).Distinct().Count());
        CollectionAssert.AreEqual(Enumerable.Range(0, docs.Count).ToArray(), docs.Select(d => d.ChildIndex).ToArray());
    }

    [TestMethod]
    public async Task EveryUnitIsAChild_UntilParentsAreIndexedSeparately()
    {
        var (docs, _, _) = await BuildService().ChunkDocumentsAsync([Doc("doc1", "content")]);

        Assert.AreEqual(ChunkGrain.Child, docs[0].Grain);
    }

    // ── sections ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task HeadingsCutTheDocumentIntoSections()
    {
        var content = "Eerste kop\n\nBody one.\n\nTweede kop\n\nBody two.";
        var doc     = Doc("doc1", content, headings: [H("Eerste kop", 0), H("Tweede kop", 25)]);

        var (docs, _, _) = await BuildService().ChunkDocumentsAsync([doc]);

        Assert.AreEqual(2, docs.Count);
        CollectionAssert.AreEqual(new[] { 0, 1 }, docs.Select(d => d.SectionIndex).ToArray());
        CollectionAssert.AreEqual(new[] { "Eerste kop", "Tweede kop" }, docs.Select(d => d.HeadingText).ToArray());
    }

    [TestMethod]
    public async Task ContentBeforeTheFirstHeading_BecomesItsOwnSection()
    {
        // The profile is not decoration here: with ONE heading, the gate's first clause (>= 2
        // headings) fails, so the document reaches route 1 only through the small-document
        // clause - and a null profile deliberately does not take it, because null < int is
        // false and a missing measurement must never be read as "small".
        var content = "Cover text.\n\nHoofdstuk 1\n\nBody.";
        var doc     = Doc("doc1", content, headings: [H("Hoofdstuk 1", 0)], profile: Profile(hasContent: true));

        var (docs, _, _) = await BuildService().ChunkDocumentsAsync([doc]);

        Assert.AreEqual(2, docs.Count);
        Assert.IsNull(docs[0].HeadingText);
        Assert.AreEqual(ChunkHeadingSource.None, docs[0].HeadingSource);
        Assert.IsFalse(docs[0].HeadingLocated, "a preamble is a real section with no heading, not a located one");
    }

    [TestMethod]
    public async Task NoHeadingsAnywhere_StillProducesChunks_ViaTheDegenerateSingleSection()
    {
        // Branch 5 of the cascade. It has no route of its own precisely so that it cannot be
        // forgotten - it is the normal path with zero located headings.
        var (docs, _, _) = await BuildService().ChunkDocumentsAsync([Doc("doc1", "Just prose, no headings.")]);

        Assert.AreEqual(1, docs.Count);
        Assert.IsNull(docs[0].HeadingText);
    }

    // ── parent text ──────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ParentText_IsNull_WhenTheSectionWasNotSplit()
    {
        // The child IS the section here, so a copy would be byte-for-byte identical to
        // Content. Phase A measured 83-87% of sections as never split, so storing it
        // unconditionally would roughly double the corpus's stored text to say nothing.
        var (docs, _, _) = await BuildService().ChunkDocumentsAsync([Doc("doc1", "Short body.")]);

        Assert.IsNull(docs[0].ParentText);
    }

    [TestMethod]
    public async Task ASplitSection_IdentifiesItsParentByIdAndOrdinals_NotByACopyOfItsText()
    {
        // ParentText has NO producer under the two-strategy design - draft §5.5 replaced the
        // materialized parent with parent_id plus ordinals and a structural window that slices
        // the source at query time. The field survives only so a snapshot written before that
        // change still round-trips, which is why this asserts it stays null rather than
        // asserting it is filled.
        var body = string.Join(" ", Enumerable.Repeat("woord", 400));
        var (docs, _, _) = await BuildService().ChunkDocumentsAsync([Doc("doc1", body)]);

        Assert.IsTrue(docs.Count > 1, "the body has to exceed the ceiling for there to be a split at all");
        Assert.IsTrue(docs.All(d => d.ParentText is null));

        // What replaced it: one SectionId shared by every child, and ordinals that place each
        // child inside it.
        Assert.AreEqual(1, docs.Select(d => d.SectionId).Distinct().Count());
        CollectionAssert.AreEqual(
            Enumerable.Range(0, docs.Count).ToArray(),
            docs.Select(d => d.ChildIndex).ToArray());
    }

    // ── the embedded prefix ──────────────────────────────────────────────────

    [TestMethod]
    public async Task Title_IsPrependedToTheEmbeddedText()
    {
        // The prefix rides in EmbeddingText, not in Content. Content is the BARE BODY so that
        // Content == doc.Content[Start..(Start + Length)] stays assertable - page attribution,
        // the snapshot round-trip and the minimum-content rule all read that invariant.
        var (docs, _, _) = await BuildService().ChunkDocumentsAsync([Doc("doc1", "body text", title: "My Title")]);

        Assert.AreEqual("body text", docs[0].Content);
        Assert.AreEqual("My Title", docs[0].Prefix);
        Assert.IsTrue(docs[0].EmbeddingText.StartsWith("My Title", StringComparison.Ordinal));
        Assert.IsTrue(docs[0].EmbeddingText.EndsWith("body text", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task NoTitleOrHeading_LeavesTheBodyUnprefixed()
    {
        var (docs, _, _) = await BuildService().ChunkDocumentsAsync([Doc("doc1", "body text")]);

        Assert.AreEqual("body text", docs[0].Content);
    }

    [TestMethod]
    public async Task HeadingPath_IsPrependedAfterTheTitle()
    {
        // Route 1 only - there is no heading path on the recursive route - so the profile is
        // what gets this single-heading document onto that route at all.
        var content = "Hoofdstuk 1\n\nBody.";
        var doc     = Doc("doc1", content, title: "Doc", headings: [H("Hoofdstuk 1", 0)],
                          profile: Profile(hasContent: true));

        var (docs, _, _) = await BuildService().ChunkDocumentsAsync([doc]);

        Assert.IsTrue(docs[0].EmbeddingText.StartsWith("Doc\n\nHoofdstuk 1", StringComparison.Ordinal));
        Assert.IsFalse(docs[0].Content.StartsWith("Doc", StringComparison.Ordinal), "the body is not prefixed in place");
    }

    [TestMethod]
    public async Task SectorTag_IsInTheEmbeddedText_NotOnlyInAFilterableField()
    {
        // The dangerous failure here is a well-formed, on-topic, WRONG-SECTOR answer, which no
        // similarity score can flag. The filter is the deterministic fix; putting the tag in
        // the embedded text as well pushes the signal into the vector. It has to be in from
        // the first build - adding it later changes every vector.
        var (docs, _, _) = await BuildService().ChunkDocumentsAsync([Doc("doc1", "body", title: "CAO GGZ 2025")]);

        Assert.AreEqual("GGZ", docs[0].DomainTag);
        Assert.IsTrue(docs[0].EmbeddingText.Contains("[GGZ]", StringComparison.Ordinal),
            "in the vector as well as in the filterable field - the tag is priced into the prefix before the cut");
        Assert.IsFalse(docs[0].Content.Contains("[GGZ]", StringComparison.Ordinal),
            "but not written into the body, which stays a slice of the source");
    }

    // ── pages ────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task PageStartAndEnd_ComeFromThePageMap()
    {
        var (docs, _, _) = await BuildService().ChunkDocumentsAsync([Doc("doc1", "content", page: 5)]);

        Assert.AreEqual(5, docs[0].PageStart);
        Assert.AreEqual(5, docs[0].PageEnd);
    }

    [TestMethod]
    public async Task ChunkSpanningTwoPages_ReportsBothEnds()
    {
        // The reason page_start/page_end replaced a single page_number: once sections are the
        // grain, a chunk can start on one page and finish on the next.
        var content = "First page text. Second page text.";
        var doc = Doc("doc1", content) with
        {
            PageSpans =
            [
                new PageSpan(1, 0,  17, null, false),
                new PageSpan(2, 17, 17, null, false),
            ],
        };

        var (docs, _, _) = await BuildService().ChunkDocumentsAsync([doc]);

        Assert.AreEqual(1, docs[0].PageStart);
        Assert.AreEqual(2, docs[0].PageEnd);
    }

    [TestMethod]
    public async Task PictureOnlyPage_FlagsTheChunkCoveringIt()
    {
        var doc = Doc("doc1", "text") with
        {
            PageSpans = [new PageSpan(1, 0, 4, null, IsPictureOnly: true)],
        };

        var (docs, _, _) = await BuildService().ChunkDocumentsAsync([doc]);

        Assert.IsTrue(docs[0].PageExtractionFlag);
    }

    // ── gates ────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ExtractionGateFailure_ProducesNoChunks_RatherThanVectorResidue()
    {
        // A document with no extractable text produces vector-residue chunks (the corpus has a
        // literal "£ £" 30-character chunk). Emitting those is worse than emitting nothing.
        var doc = Doc("doc1", "£ £", profile: Profile(hasContent: false));

        var (docs, _, _) = await BuildService().ChunkDocumentsAsync([doc]);

        Assert.AreEqual(0, docs.Count);
    }

    [TestMethod]
    public async Task NoProfileComputed_IsTreatedAsHavingContent()
    {
        // A missing measurement must never silently drop a document.
        var (docs, _, _) = await BuildService().ChunkDocumentsAsync([Doc("doc1", "body", profile: null)]);

        Assert.AreEqual(1, docs.Count);
    }

    // ── carried fields ───────────────────────────────────────────────────────

    [TestMethod]
    public async Task ExtractionFieldsAreMappedOntoTheChunk()
    {
        var created = DateTimeOffset.Parse("2020-01-01T00:00:00Z");
        var mod     = DateTimeOffset.Parse("2024-06-01T00:00:00Z");
        var last    = DateTimeOffset.Parse("2026-08-01T00:00:00Z");

        var doc = Doc("doc1", "body", title: "T", author: "mherbst",
            createdAt: created, modDate: mod, pageCount: 12, lastModifiedDate: last,
            zenyaDocumentId: "Z1", zenyaVersion: "3", zenyaStatus: "actief", zenyaUrl: "https://z",
            language: "nl",
            tables: [new TableInfo(2, 3, [], null, 1, null, [], [])]);

        var (docs, _, _) = await BuildService().ChunkDocumentsAsync([doc]);
        var chunk = docs[0];

        Assert.AreEqual("doc1", chunk.DocumentId);
        Assert.AreEqual("T",    chunk.Title);
        Assert.AreEqual("mherbst", chunk.Metadata.Author);
        Assert.AreEqual(created, chunk.CreatedAt);
        Assert.AreEqual(mod,     chunk.ModDate);
        Assert.AreEqual(last,    chunk.LastModifiedDate);
        Assert.AreEqual(12,      chunk.PageCount);
        Assert.AreEqual("Z1",    chunk.ZenyaDocumentId);
        Assert.AreEqual("3",     chunk.ZenyaVersion);
        Assert.AreEqual("actief", chunk.ZenyaStatus);
        Assert.AreEqual("https://z", chunk.ZenyaUrl);
        Assert.AreEqual("nl",    chunk.Language);
        Assert.AreEqual(1,       chunk.TableCount);

        // TableCount and HasTable answer DIFFERENT questions, deliberately. TableCount is
        // page-scoped - extraction found a table on the pages this cut covers - and is stamped
        // in step 4. HasTable is computed from Content and asks whether THIS chunk contains
        // one, which is the narrower claim that survives a restore. A prose chunk sharing a
        // page with a table has TableCount 1 and HasTable false, and that is not a
        // disagreement.
        Assert.IsFalse(chunk.HasTable, "the body carries no markdown table of its own");
    }

    [TestMethod]
    public async Task TokenCount_IsTheRealCountOverTheEmbeddedText()
    {
        // Over the EMBEDDED text - prefix included - not over Content. That is the number the
        // 512-token ceiling governs, and counting the body alone would understate every chunk
        // by the cost of its own prefix.
        var (docs, _, _) = await BuildService().ChunkDocumentsAsync([Doc("doc1", "body text", title: "My Title")]);

        Assert.AreEqual(TokenCounter.Count(docs[0].EmbeddingText), docs[0].TokenCount);
        Assert.AreNotEqual(TokenCounter.Count(docs[0].Content), docs[0].TokenCount);
    }

    [TestMethod]
    public async Task ChunksComeBackInInputDocumentOrder()
    {
        // The chunk list preserves the order the documents arrived in - the loop never sorts,
        // and a caller that needs a stable order across runs is looking at the run REPORT,
        // whose rows are ordered by SourceId (see ChunkingReporterTests). Asserting a sorted
        // chunk list here asserted an ordering nothing produces.
        //
        // Bodies have to clear the minimum-content rule, or every unit is dropped as vector
        // residue and there is nothing left to assert an order on.
        var docs = new[]
        {
            Doc("docC", "Body of the third document."),
            Doc("docA", "Body of the first document."),
            Doc("docB", "Body of the second document."),
        };

        var (result, _, _) = await BuildService().ChunkDocumentsAsync(docs);

        CollectionAssert.AreEqual(
            new[] { "Body of the third document.", "Body of the first document.", "Body of the second document." },
            result.Select(d => d.Content).ToList());
    }

    [TestMethod]
    public async Task NoDocuments_ReturnsEmpty()
    {
        var (docs, stats, _) = await BuildService().ChunkDocumentsAsync([]);

        Assert.AreEqual(0, docs.Count);
        Assert.AreEqual(0, stats.ChunksProduced);
    }

    [TestMethod]
    public async Task StatsCarryTheStrategyNameAndChunkCount()
    {
        var (docs, stats, _) = await BuildService().ChunkDocumentsAsync([Doc("doc1", "body")]);

        Assert.AreEqual("TwoAxisChunking", stats.Strategy);
        Assert.AreEqual(docs.Count, stats.ChunksProduced);
    }

    // ── run report ────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task RunReport_IsWritten_WithOneRowPerDocument()
    {
        var (service, reports) = BuildWithReports();

        await service.ChunkDocumentsAsync(
            [Doc("docA", "body a", title: "CAO GGZ"), Doc("docB", "body b")], "instance-1");

        var report = reports.Single();
        Assert.IsTrue(report.Success);
        Assert.IsNull(report.FailedAtStage);
        Assert.AreEqual("instance-1", report.InstanceId);
        CollectionAssert.AreEqual(new[] { "docA", "docB" }, report.Documents.Select(d => d.SourceId).ToList());
        Assert.IsTrue(report.Documents.All(d => d.Outcome == "chunked"));
        Assert.IsTrue(report.Documents.All(d => d.ChunkCount > 0));
    }

    [TestMethod]
    public async Task RunReport_CarriesResolvedIdentityPerDocument()
    {
        var (service, reports) = BuildWithReports();

        await service.ChunkDocumentsAsync([Doc("doc1", "body", title: "CAO GGZ 2025")], "instance-1");

        var row = reports.Single().Documents.Single();
        Assert.AreEqual("GGZ", row.DomainTag);
        Assert.AreEqual("doc1", row.FamilyId);
        // Nothing else in the run to cluster with, so this is a family of one - the flag is
        // what distinguishes that from a real near-duplicate group.
        Assert.IsFalse(row.IsInMultiMemberFamily);
        Assert.AreEqual("embedded", row.IdentityVectorSource);
    }

    [TestMethod]
    public async Task RunReport_DocumentFailingTheExtractionGate_IsFlaggedButStillChunked()
    {
        // The gate was demoted from filter to flag on 260814: it used to drop the document,
        // which is how 20 of 51 documents were absent from the index while every stage reported
        // success (calibration-findings.md §1 measured all 20, several at 3,000+ chars/page -
        // they were failing on bytes/char, not on being text-poor).
        //
        // The document now chunks, and the gate's verdict travels as a flag on its report row -
        // still visible, no longer destructive.
        var (service, reports) = BuildWithReports();
        var doc = Doc("gated", "body", profile: Profile(hasContent: false));

        var (chunks, _, _) = await service.ChunkDocumentsAsync([doc], "instance-1");

        Assert.IsTrue(chunks.Count > 0, "a gated document must still produce chunks");

        var row = reports.Single().Documents.Single();
        Assert.AreEqual("chunked", row.Outcome);
        Assert.IsTrue(row.FailedExtractionGate, "the gate's verdict must still be reported");
        Assert.AreEqual(chunks.Count, row.ChunkCount);
    }

    [TestMethod]
    public async Task RunReport_IsStillWritten_WhenTheStageThrows()
    {
        // The whole point of writing from a finally: identity resolution throws on a
        // wrong-dimension vector, and that run is exactly the one worth diagnosing.
        var client = new Mock<IEmbeddingClient>();
        client
            .Setup(c => c.EmbedWithRetryAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new float[][] { [1f, 0f] }, 0));

        var store = new Mock<IDocumentIdentityStore>();
        store.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var resolver = new DocumentIdentityResolver(
            client.Object, store.Object,
            new IndexerConfig { OpenAiEmbeddingDimensions = 3 },
            NullLogger<DocumentIdentityResolver>.Instance);

        var (service, reports) = BuildWithReports(resolver: resolver);

        // Needs a title: a document with no title and no headings has nothing to embed, so
        // resolution would skip it and never reach the dimension check.
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.ChunkDocumentsAsync([Doc("doc1", "body", title: "CAO GGZ")], "instance-1"));

        var report = reports.Single();
        Assert.IsFalse(report.Success);
        Assert.AreEqual("identity-resolution", report.FailedAtStage);
        StringAssert.Contains(report.Error, "expected 3");

        // The document is still accounted for, as never-reached rather than missing.
        var row = report.Documents.Single();
        Assert.AreEqual("doc1", row.SourceId);
        Assert.AreEqual("not_reached", row.Outcome);
    }

    [TestMethod]
    public async Task RunReport_WriteFailure_DoesNotFailTheStage()
    {
        var writer = new Mock<IPipelineArtifactWriter>();
        writer
            .Setup(w => w.WriteArtifactAsync(It.IsAny<string>(), It.IsAny<ChunkingRunReport>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("blob storage is down"));

        var service = BuildChunkingService(
            ChunkingBudget.TokenCeiling, BuildDocumentIdentityResolver(), writer.Object);

        var (chunks, _, _) = await service.ChunkDocumentsAsync([Doc("doc1", "body")], "instance-1");

        Assert.AreEqual(1, chunks.Count);
    }
}
