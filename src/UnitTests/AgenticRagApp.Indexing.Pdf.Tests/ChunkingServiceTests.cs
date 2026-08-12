using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Infrastructure.Clients.Embedding;
using AgenticRagApp.Infrastructure.Configuration;
using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Indexing.Pdf.Services;
using AgenticRagApp.Indexing.Pdf.Utils;
using AgenticRagApp.Common.Models;

namespace RagApp.UnitTests.Indexing;

// ChunkingService no longer wraps a flat Chunk(string) strategy, so these run the real
// section cascade end to end rather than a mocked splitter. That is deliberate: the thing
// worth testing here is how a cut becomes an indexed row (ids, prefix, page attribution,
// parent text), and a mock that returns "one chunk per document" cannot exercise any of it.
[TestClass]
public class ChunkingServiceTests
{
    // No persisted identity; the embedding call echoes back one arbitrary vector per input.
    // ChunkingService's own behaviour is what these exercise, not FamilyIdEmbedder's
    // clustering (see FamilyIdEmbedderTests for that).
    private static FamilyIdEmbedder BuildFamilyIdEmbedder()
    {
        var embeddingClient = new Mock<IEmbeddingClient>();
        embeddingClient
            .Setup(c => c.EmbedWithRetryAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyList<string>, CancellationToken>((texts, _) =>
                Task.FromResult((texts.Select(_ => new float[] { 1f, 0f, 0f }).ToArray(), 0)));

        var store = new Mock<IDocumentIdentityStore>();
        store.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        return new FamilyIdEmbedder(embeddingClient.Object, store.Object, new IndexerConfig(), NullLogger<FamilyIdEmbedder>.Instance);
    }

    private static ChunkingService BuildService(int tokenCeiling = SectionSplitter.DefaultTokenCeiling)
    {
        var cascade  = new SectionCascadeStrategy(new SectionSplitter(), tokenCeiling);
        var selector = new DocumentStrategySelector(cascade, NullLogger<DocumentStrategySelector>.Instance);

        return new ChunkingService(selector, BuildFamilyIdEmbedder(), NullLogger<ChunkingService>.Instance);
    }

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
        DocumentRouting?        routing          = null,
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
            Routing:          routing,
            Language:         language);

    private static Heading H(string content, int offset, int page = 1, int depth = 1) =>
        new(content, "sectionHeading", offset, page, depth);

    private static DocumentRouting Routing(bool hasContent) =>
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
        var (docs, _) = await BuildService().ChunkDocumentsAsync([Doc("doc1", "content", page: 7)]);

        Assert.AreEqual(ChunkingHelper.SafeKey("doc1::s0", 0), docs[0].Id);
    }

    [TestMethod]
    public async Task SectionId_IsSynthesized_AndSharedByEveryChildOfOneSection()
    {
        // There is no parent row for section_id to point at - parent text is materialized onto
        // each child instead. It is a grouping key, so what matters is that siblings agree.
        var body = string.Join(" ", Enumerable.Repeat("woord", 400));
        var (docs, _) = await BuildService(tokenCeiling: 60).ChunkDocumentsAsync([Doc("doc1", body)]);

        Assert.IsTrue(docs.Count > 1, "expected the section to be split");
        Assert.AreEqual(1, docs.Select(d => d.SectionId).Distinct().Count());
        CollectionAssert.AreEqual(Enumerable.Range(0, docs.Count).ToArray(), docs.Select(d => d.ChildIndex).ToArray());
    }

    [TestMethod]
    public async Task EveryUnitIsAChild_UntilParentsAreIndexedSeparately()
    {
        var (docs, _) = await BuildService().ChunkDocumentsAsync([Doc("doc1", "content")]);

        Assert.AreEqual(ChunkGrain.Child, docs[0].Grain);
    }

    // ── sections ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task HeadingsCutTheDocumentIntoSections()
    {
        var content = "Eerste kop\n\nBody one.\n\nTweede kop\n\nBody two.";
        var doc     = Doc("doc1", content, headings: [H("Eerste kop", 0), H("Tweede kop", 25)]);

        var (docs, _) = await BuildService().ChunkDocumentsAsync([doc]);

        Assert.AreEqual(2, docs.Count);
        CollectionAssert.AreEqual(new[] { 0, 1 }, docs.Select(d => d.SectionIndex).ToArray());
        CollectionAssert.AreEqual(new[] { "Eerste kop", "Tweede kop" }, docs.Select(d => d.HeadingText).ToArray());
    }

    [TestMethod]
    public async Task ContentBeforeTheFirstHeading_BecomesItsOwnSection()
    {
        var content = "Cover text.\n\nHoofdstuk 1\n\nBody.";
        var doc     = Doc("doc1", content, headings: [H("Hoofdstuk 1", 0)]);

        var (docs, _) = await BuildService().ChunkDocumentsAsync([doc]);

        Assert.AreEqual(2, docs.Count);
        Assert.IsNull(docs[0].HeadingText);
        Assert.AreEqual(ChunkHeadingSource.None, docs[0].HeadingSource);
    }

    [TestMethod]
    public async Task NoHeadingsAnywhere_StillProducesChunks_ViaTheDegenerateSingleSection()
    {
        // Branch 5 of the cascade. It has no route of its own precisely so that it cannot be
        // forgotten - it is the normal path with zero located headings.
        var (docs, _) = await BuildService().ChunkDocumentsAsync([Doc("doc1", "Just prose, no headings.")]);

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
        var (docs, _) = await BuildService().ChunkDocumentsAsync([Doc("doc1", "Short body.")]);

        Assert.IsNull(docs[0].ParentText);
    }

    [TestMethod]
    public async Task ParentText_IsTheWholeSection_WhenItWasSplit()
    {
        var body = string.Join(" ", Enumerable.Repeat("woord", 400));
        var (docs, _) = await BuildService(tokenCeiling: 60).ChunkDocumentsAsync([Doc("doc1", body)]);

        Assert.IsTrue(docs.Count > 1);
        Assert.IsTrue(docs.All(d => d.ParentText is not null));
        Assert.AreEqual(1, docs.Select(d => d.ParentText).Distinct().Count());
        Assert.IsTrue(docs[0].ParentText!.Length > docs[0].Content.Length);
    }

    // ── the embedded prefix ──────────────────────────────────────────────────

    [TestMethod]
    public async Task Title_IsPrependedToTheEmbeddedText()
    {
        var (docs, _) = await BuildService().ChunkDocumentsAsync([Doc("doc1", "body text", title: "My Title")]);

        Assert.IsTrue(docs[0].Content.StartsWith("My Title", StringComparison.Ordinal));
        Assert.IsTrue(docs[0].Content.EndsWith("body text", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task NoTitleOrHeading_LeavesTheBodyUnprefixed()
    {
        var (docs, _) = await BuildService().ChunkDocumentsAsync([Doc("doc1", "body text")]);

        Assert.AreEqual("body text", docs[0].Content);
    }

    [TestMethod]
    public async Task HeadingPath_IsPrependedAfterTheTitle()
    {
        var content = "Hoofdstuk 1\n\nBody.";
        var doc     = Doc("doc1", content, title: "Doc", headings: [H("Hoofdstuk 1", 0)]);

        var (docs, _) = await BuildService().ChunkDocumentsAsync([doc]);

        Assert.IsTrue(docs[0].Content.StartsWith("Doc\n\nHoofdstuk 1", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SectorTag_IsInTheEmbeddedText_NotOnlyInAFilterableField()
    {
        // The dangerous failure here is a well-formed, on-topic, WRONG-SECTOR answer, which no
        // similarity score can flag. The filter is the deterministic fix; putting the tag in
        // the embedded text as well pushes the signal into the vector. It has to be in from
        // the first build - adding it later changes every vector.
        var (docs, _) = await BuildService().ChunkDocumentsAsync([Doc("doc1", "body", title: "CAO GGZ 2025")]);

        Assert.AreEqual("GGZ", docs[0].DomainTag);
        Assert.IsTrue(docs[0].Content.Contains("[GGZ]", StringComparison.Ordinal));
    }

    // ── pages ────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task PageStartAndEnd_ComeFromThePageMap()
    {
        var (docs, _) = await BuildService().ChunkDocumentsAsync([Doc("doc1", "content", page: 5)]);

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

        var (docs, _) = await BuildService().ChunkDocumentsAsync([doc]);

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

        var (docs, _) = await BuildService().ChunkDocumentsAsync([doc]);

        Assert.IsTrue(docs[0].PageExtractionFlag);
    }

    // ── gates ────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ExtractionGateFailure_ProducesNoChunks_RatherThanVectorResidue()
    {
        // A document with no extractable text produces vector-residue chunks (the corpus has a
        // literal "£ £" 30-character chunk). Emitting those is worse than emitting nothing.
        var doc = Doc("doc1", "£ £", routing: Routing(hasContent: false));

        var (docs, _) = await BuildService().ChunkDocumentsAsync([doc]);

        Assert.AreEqual(0, docs.Count);
    }

    [TestMethod]
    public async Task NoRoutingComputed_IsTreatedAsHavingContent()
    {
        // A missing measurement must never silently drop a document.
        var (docs, _) = await BuildService().ChunkDocumentsAsync([Doc("doc1", "body", routing: null)]);

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

        var (docs, _) = await BuildService().ChunkDocumentsAsync([doc]);
        var chunk = docs[0];

        Assert.AreEqual("doc1", chunk.DocumentId);
        Assert.AreEqual("T",    chunk.Title);
        Assert.AreEqual("mherbst", chunk.Author);
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
        Assert.IsTrue(chunk.HasTable);
    }

    [TestMethod]
    public async Task TokenCount_IsTheRealCountOverTheEmbeddedText()
    {
        var (docs, _) = await BuildService().ChunkDocumentsAsync([Doc("doc1", "body text", title: "My Title")]);

        Assert.AreEqual(TokenCounter.Count(docs[0].Content), docs[0].TokenCount);
    }

    [TestMethod]
    public async Task DocumentsAreOrderedBySourceId()
    {
        var docs = new[] { Doc("docC", "c"), Doc("docA", "a"), Doc("docB", "b") };

        var (result, _) = await BuildService().ChunkDocumentsAsync(docs);

        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, result.Select(d => d.Content).ToList());
    }

    [TestMethod]
    public async Task NoDocuments_ReturnsEmpty()
    {
        var (docs, stats) = await BuildService().ChunkDocumentsAsync([]);

        Assert.AreEqual(0, docs.Count);
        Assert.AreEqual(0, stats.ChunksProduced);
    }

    [TestMethod]
    public async Task StatsCarryTheStrategyNameAndChunkCount()
    {
        var (docs, stats) = await BuildService().ChunkDocumentsAsync([Doc("doc1", "body")]);

        Assert.AreEqual("TwoAxisChunking", stats.Strategy);
        Assert.AreEqual(docs.Count, stats.ChunksProduced);
    }
}
