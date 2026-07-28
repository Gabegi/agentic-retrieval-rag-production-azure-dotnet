using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using AgenticRagApp.Indexing.Csv.Services;
using AgenticRagApp.Indexing.Csv.Utils;
using AgenticRagApp.Common.Models;

namespace RagApp.UnitTests.CsvExtraction;

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

    private static ExtractionDocument Doc(
        string sourceId, int ordinal, string content, Dictionary<string, string>? metadata = null) =>
        new(sourceId, ordinal, content, metadata ?? []);

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
        var doc     = Doc("doc1", 0, "body text", new() { ["title"] = "My Title" });

        var (docs, _) = service.ChunkDocuments([doc]);

        Assert.AreEqual("My Title\n\nbody text", docs[0].Content);
        Assert.AreEqual("My Title", docs[0].Title);
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
    public void ChunkDocuments_HeadingFromStrategy_IsInsertedBetweenTitleAndBody()
    {
        var strategy = MockStrategy(chunkFn: _ => [new TextChunk(0, "body text", Heading: "Section 1")]);
        var service  = BuildService(strategy);
        var doc      = Doc("doc1", 0, "body text", new() { ["title"] = "My Title" });

        var (docs, _) = service.ChunkDocuments([doc]);

        Assert.AreEqual("My Title\n\nSection 1\n\nbody text", docs[0].Content);
        Assert.AreEqual("Section 1", docs[0].Heading);
    }

    [TestMethod]
    public void ChunkDocuments_SummaryIsKeptOutOfContent_ButSetOnSummaryField()
    {
        var service = BuildService(MockStrategy());
        var doc     = Doc("doc1", 0, "body text", new() { ["summary"] = "A summary" });

        var (docs, _) = service.ChunkDocuments([doc]);

        Assert.AreEqual("body text", docs[0].Content);
        Assert.AreEqual("A summary", docs[0].Summary);
    }

    [TestMethod]
    public void ChunkDocuments_WhitespaceSummary_IsNulledOut()
    {
        var service = BuildService(MockStrategy());
        var doc     = Doc("doc1", 0, "body text", new() { ["summary"] = "   " });

        var (docs, _) = service.ChunkDocuments([doc]);

        Assert.IsNull(docs[0].Summary);
    }

    [TestMethod]
    public void ChunkDocuments_MapsMetadataFieldsOntoProtocolDocument()
    {
        var service = BuildService(MockStrategy());
        var doc = Doc("doc1", 0, "content", new()
        {
            ["title"]               = "Title",
            ["folder_path"]         = "Folder/Path",
            ["quick_code"]          = "QC1",
            ["relative_path"]       = "rel/path",
            ["last_modified_date"]  = "2024-05-01T00:00:00Z",
            ["check_date"]          = "2020-01-01T00:00:00Z",
            ["version"]             = "7.0",
        });

        var (docs, _) = service.ChunkDocuments([doc]);

        var result = docs[0];
        Assert.AreEqual("Title", result.Title);
        Assert.AreEqual("Folder/Path", result.Department);
        Assert.AreEqual("QC1", result.QuickCode);
        Assert.AreEqual("rel/path", result.RelativePath);
        Assert.AreEqual(DateTimeOffset.Parse("2024-05-01T00:00:00Z"), result.LastModifiedDate);
        Assert.AreEqual(DateTimeOffset.Parse("2020-01-01T00:00:00Z"), result.CheckDate);
        Assert.AreEqual("7.0", result.Version);
    }

    [TestMethod]
    public void ChunkDocuments_UnparseableDate_IsNull()
    {
        var service = BuildService(MockStrategy());
        var doc     = Doc("doc1", 0, "content", new() { ["last_modified_date"] = "not-a-date" });

        var (docs, _) = service.ChunkDocuments([doc]);

        Assert.IsNull(docs[0].LastModifiedDate);
    }

    [TestMethod]
    public void ChunkDocuments_ChunkIndexIsScopedPerDocument_NotAcrossRun()
    {
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
