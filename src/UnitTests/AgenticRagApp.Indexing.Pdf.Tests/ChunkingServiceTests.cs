using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Indexing.Pdf.Services;
using AgenticRagApp.Indexing.Pdf.Utils;
using AgenticRagApp.Common.Models;

namespace RagApp.UnitTests.Indexing;

[TestClass]
public class ChunkingServiceTests
{
    private static Mock<IChunkingStrategy> MockStrategy(string name = "TestStrategy", Func<string, IReadOnlyList<TextChunk>>? chunkFn = null)
    {
        var mock = new Mock<IChunkingStrategy>();
        mock.SetupGet(s => s.Name).Returns(name);
        mock.Setup(s => s.Chunk(It.IsAny<string>()))
            .Returns<string>(content => chunkFn?.Invoke(content) ?? [new TextChunk(0, content)]);
        return mock;
    }

    private static ChunkingService BuildService(Mock<IChunkingStrategy> strategy) =>
        new(strategy.Object, NullLogger<ChunkingService>.Instance);

    private static PdfExtractionDocument Doc(
        string sourceId, int ordinal, string content,
        string                  title            = "",
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
        string?                 breadcrumb       = null,
        IReadOnlyList<Heading>? headings         = null,
        IReadOnlyList<Heading>? boilerplate      = null,
        IReadOnlyList<TableInfo>? tables         = null,
        PageDimensions?         dimensions       = null,
        IReadOnlyList<SelectionMarkInfo>? selectionMarks = null,
        IReadOnlyList<FigureInfo>? figures       = null,
        IReadOnlyList<LineInfo>? lines           = null) =>
        new(
            SourceId:              sourceId,
            Ordinal:               ordinal,
            Content:               content,
            Title:                 title,
            Author:                author,
            CreatedAt:             createdAt,
            ModDate:               modDate,
            PageCount:             pageCount,
            LastModifiedDate:      lastModifiedDate,
            ZenyaDocumentId:       zenyaDocumentId,
            ZenyaVersion:          zenyaVersion,
            ZenyaStatus:           zenyaStatus,
            ZenyaUrl:              zenyaUrl,
            Bookmarks:             bookmarks ?? [],
            Sections:              sections ?? [],
            Breadcrumb:            breadcrumb,
            Headings:              headings ?? [],
            Boilerplate:           boilerplate ?? [],
            Tables:                tables ?? [],
            Dimensions:            dimensions,
            SelectionMarks:        selectionMarks ?? [],
            Figures:               figures ?? [],
            Lines:                 lines ?? []);

    [TestMethod]
    public void Name_PassesThroughFromStrategy()
    {
        var service = BuildService(MockStrategy(name: "MyStrategy"));

        Assert.AreEqual("MyStrategy", service.Name);
    }

    [TestMethod]
    public void Chunk_EmptyContent_ReturnsEmptyWithoutCallingStrategy()
    {
        var strategy = MockStrategy();
        var service  = BuildService(strategy);

        var result = service.Chunk("");

        Assert.AreEqual(0, result.Count);
        strategy.Verify(s => s.Chunk(It.IsAny<string>()), Times.Never);
    }

    [TestMethod]
    public void Chunk_WhitespaceContent_ReturnsEmptyWithoutCallingStrategy()
    {
        var strategy = MockStrategy();
        var service  = BuildService(strategy);

        var result = service.Chunk("   \t\n");

        Assert.AreEqual(0, result.Count);
        strategy.Verify(s => s.Chunk(It.IsAny<string>()), Times.Never);
    }

    [TestMethod]
    public void Chunk_NonEmptyContent_DelegatesToStrategy()
    {
        var strategy = MockStrategy();
        var service  = BuildService(strategy);

        var result = service.Chunk("hello world");

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("hello world", result[0].Content);
        strategy.Verify(s => s.Chunk("hello world"), Times.Once);
    }

    [TestMethod]
    public void ChunkDocuments_ComputesIdFromSourceIdOrdinalAndChunkIndex()
    {
        var service = BuildService(MockStrategy());
        var doc     = Doc("doc1", ordinal: 2, content: "content");

        var (docs, _) = service.ChunkDocuments([doc]);

        var expectedId = ChunkingUtils.SafeKey("doc1::2", 0);
        Assert.AreEqual(expectedId, docs[0].Id);
    }

    [TestMethod]
    public void ChunkDocuments_SetsDocumentIdAndPageNumberFromSourceDocument()
    {
        var service = BuildService(MockStrategy());
        var doc     = Doc("doc1", ordinal: 5, content: "content");

        var (docs, _) = service.ChunkDocuments([doc]);

        Assert.AreEqual("doc1", docs[0].DocumentId);
        Assert.AreEqual(5, docs[0].PageNumber);
        Assert.AreEqual(0, docs[0].ChunkIndex);
    }

    [TestMethod]
    public void ChunkDocuments_PrependsTitleToContent_WhenTitlePresent()
    {
        var service = BuildService(MockStrategy());
        var doc     = Doc("doc1", 0, "body text", title: "My Title");

        var (docs, _) = service.ChunkDocuments([doc]);

        Assert.AreEqual("My Title\n\nbody text", docs[0].Content);
    }

    [TestMethod]
    public void ChunkDocuments_NoTitle_ContentIsBodyOnly()
    {
        var service = BuildService(MockStrategy());
        var doc     = Doc("doc1", 0, "body text");

        var (docs, _) = service.ChunkDocuments([doc]);

        Assert.AreEqual("body text", docs[0].Content);
    }

    [TestMethod]
    public void ChunkDocuments_PrependsBreadcrumbBeforeTitle_WhenPresent()
    {
        var service = BuildService(MockStrategy());
        var doc     = Doc("doc1", 0, "body text", title: "My Title", breadcrumb: "_Section: Chapter 1_");

        var (docs, _) = service.ChunkDocuments([doc]);

        Assert.AreEqual("My Title\n\n_Section: Chapter 1_\n\nbody text", docs[0].Content);
        Assert.AreEqual("_Section: Chapter 1_", docs[0].Heading);
    }

    [TestMethod]
    public void ChunkDocuments_FallsBackToFirstDetectedHeading_WhenNoBreadcrumb()
    {
        var service = BuildService(MockStrategy());
        var doc     = Doc("doc1", 0, "body text",
            headings: [new Heading("Detected Heading", "sectionHeading", Offset: 0, PageNumber: 0)]);

        var (docs, _) = service.ChunkDocuments([doc]);

        Assert.AreEqual("Detected Heading", docs[0].Heading);
        Assert.IsTrue(docs[0].Content.Contains("Detected Heading"));
    }

    [TestMethod]
    public void ChunkDocuments_NoBreadcrumbOrHeadings_HeadingIsNull()
    {
        var service = BuildService(MockStrategy());
        var doc     = Doc("doc1", 0, "body text");

        var (docs, _) = service.ChunkDocuments([doc]);

        Assert.IsNull(docs[0].Heading);
        Assert.AreEqual("body text", docs[0].Content);
    }

    [TestMethod]
    public void ChunkDocuments_MapsExtractionFieldsOntoDocumentChunk()
    {
        var service   = BuildService(MockStrategy());
        var createdAt = DateTimeOffset.Parse("2020-01-01T00:00:00Z");
        var modDate   = DateTimeOffset.Parse("2023-06-15T00:00:00Z");
        var lastMod   = DateTimeOffset.Parse("2024-05-01T00:00:00Z");
        var table     = new TableInfo(2, 2, [], Offset: null, PageNumber: 0, Caption: null, Footnotes: [], Regions: []);
        var doc       = Doc("doc1", 0, "content",
            title:            "Title",
            author:           "J. Doe",
            createdAt:        createdAt,
            modDate:          modDate,
            pageCount:        12,
            lastModifiedDate: lastMod,
            tables:           [table]);

        var (docs, _) = service.ChunkDocuments([doc]);

        var result = docs[0];
        Assert.AreEqual("Title", result.Title);
        Assert.AreEqual("J. Doe", result.Author);
        Assert.AreEqual(createdAt, result.CreatedAt);
        Assert.AreEqual(modDate, result.ModDate);
        Assert.AreEqual(12, result.PageCount);
        Assert.AreEqual(lastMod, result.LastModifiedDate);
        Assert.AreEqual(1, result.Tables.Count);
    }

    [TestMethod]
    public void ChunkDocuments_ChunkIndexIsScopedPerDocument_NotAcrossRun()
    {
        // Two docs, each producing 2 chunks — chunk index must restart at 0 for the second
        // document rather than continuing from the first (see comment in ChunkingService).
        var strategy = MockStrategy(chunkFn: content => [new TextChunk(0, content + "-a"), new TextChunk(1, content + "-b")]);
        var service  = BuildService(strategy);
        var docs     = new[] { Doc("doc1", 0, "x"), Doc("doc2", 0, "y") };

        var (result, _) = service.ChunkDocuments(docs);

        var doc2Chunks = result.Where(d => d.DocumentId == "doc2").OrderBy(d => d.ChunkIndex).ToList();
        CollectionAssert.AreEqual(new[] { 0, 1 }, doc2Chunks.Select(d => d.ChunkIndex).ToList());
    }

    [TestMethod]
    public void ChunkDocuments_OrdersBySourceIdThenOrdinal()
    {
        var strategy = MockStrategy();
        var service  = BuildService(strategy);
        var docs = new[]
        {
            Doc("docB", 1, "b1"),
            Doc("docA", 2, "a2"),
            Doc("docA", 1, "a1"),
        };

        var (result, _) = service.ChunkDocuments(docs);

        CollectionAssert.AreEqual(
            new[] { "a1", "a2", "b1" },
            result.Select(d => d.Content).ToList());
    }

    [TestMethod]
    public void ChunkDocuments_NoDocuments_ReturnsEmptyStatsAndDocs()
    {
        var service = BuildService(MockStrategy(name: "Strat"));

        var (docs, stats) = service.ChunkDocuments([]);

        Assert.AreEqual(0, docs.Count);
        Assert.AreEqual(0, stats.ChunksProduced);
        Assert.AreEqual("Strat", stats.Strategy);
    }

    [TestMethod]
    public void ChunkDocuments_StatsReflectStrategyNameAndChunkCount()
    {
        var strategy = MockStrategy(name: "Strat", chunkFn: content => [new TextChunk(0, content), new TextChunk(1, content)]);
        var service  = BuildService(strategy);

        var (docs, stats) = service.ChunkDocuments([Doc("doc1", 0, "content")]);

        Assert.AreEqual(2, docs.Count);
        Assert.AreEqual(2, stats.ChunksProduced);
        Assert.AreEqual("Strat", stats.Strategy);
    }
}
