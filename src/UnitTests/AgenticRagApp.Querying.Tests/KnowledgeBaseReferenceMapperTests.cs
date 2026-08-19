using System.ClientModel.Primitives;
using System.Text.Json;
using Azure.Search.Documents.KnowledgeBases.Models;
using AgenticRagApp.Querying.Models;
using AgenticRagApp.Querying.Services;

namespace RagApp.UnitTests.Querying;

[TestClass]
public class KnowledgeBaseReferenceMapperTests
{
    // KnowledgeBaseReference is an Azure SDK response-only model (no public constructor) -
    // built via ModelReaderWriter from JSON, the SDK's documented pattern for constructing
    // these models in tests.
    private static KnowledgeBaseReference Reference(Dictionary<string, object?>? sourceData = null)
    {
        var payload = new Dictionary<string, object?> { ["type"] = "searchIndex" };
        if (sourceData is not null)
            payload["sourceData"] = sourceData;

        var json = JsonSerializer.Serialize(payload);
        return ModelReaderWriter.Read<KnowledgeBaseReference>(BinaryData.FromString(json))!;
    }

    [TestMethod]
    public void Reference_WithNoSourceData_IsSkipped()
    {
        var reference = Reference();

        var chunks = KnowledgeBaseReferenceMapper.Map([reference]);

        Assert.AreEqual(0, chunks.Count);
    }

    [TestMethod]
    public void Reference_WithoutContentField_IsSkipped()
    {
        var reference = Reference(new() { ["id"] = "chunk1" });

        var chunks = KnowledgeBaseReferenceMapper.Map([reference]);

        Assert.AreEqual(0, chunks.Count);
    }

    [TestMethod]
    public void Reference_WithBlankContent_IsSkipped()
    {
        var reference = Reference(new() { ["content"] = "   " });

        var chunks = KnowledgeBaseReferenceMapper.Map([reference]);

        Assert.AreEqual(0, chunks.Count);
    }

    [TestMethod]
    public void Reference_WithAllFields_MapsToRetrievedChunk()
    {
        // summary/quick_code/relative_path are deliberately absent: they were CSV-only
        // fields, and PDF and CSV no longer share an index (action-plan.md B2). They are
        // asserted null below rather than dropped from the test, so a future change that
        // silently starts populating them again fails here.
        var reference = Reference(new()
        {
            ["id"]            = "chunk1",
            ["document_id"]   = "doc1",
            ["title"]         = "Title",
            ["page_start"]    = 3,
            ["child_index"]   = 1,
            ["content"]       = "The chunk content",
            ["page_count"]    = 12,
            ["created_at"]    = "2018-02-01T00:00:00Z",
            ["mod_date"]      = "2023-06-15T00:00:00Z",
        });

        var chunks = KnowledgeBaseReferenceMapper.Map([reference]);

        Assert.AreEqual(1, chunks.Count);
        var chunk = chunks[0];
        Assert.AreEqual("chunk1", chunk.Id);
        Assert.AreEqual("doc1", chunk.DocumentId);
        Assert.AreEqual("Title", chunk.Title);
        Assert.IsNull(chunk.Summary);
        Assert.AreEqual(3, chunk.Page);
        Assert.AreEqual(1, chunk.ChunkIndex);
        Assert.IsNull(chunk.QuickCode);
        Assert.IsNull(chunk.RelativePath);
        Assert.AreEqual("The chunk content", chunk.Content);
        Assert.AreEqual(12, chunk.PageCount);
        Assert.AreEqual(DateTimeOffset.Parse("2018-02-01T00:00:00Z"), chunk.CreatedAt);
        Assert.AreEqual(DateTimeOffset.Parse("2023-06-15T00:00:00Z"), chunk.ModDate);
    }

    [TestMethod]
    public void HeadingPathAndDomainTag_AreMappedIntoTheContextText()
    {
        // The index stores the bare chunk body (SearchUploadChunk maps chunk.Content; the
        // embedded prefix lives only inside the vector), so ToContextText is the one place
        // the model's context gets the document identity back. It rebuilds PrefixBuilder's
        // exact composition: "Title [tag]", heading path, body, blank-line separated. The
        // sector tag is what separates CAO VVT from CAO GGZ from CAO GHZ on sector-ambiguous
        // questions - the old "[Title]" header dropped it (260818 eval, cao-ambig scenarios).
        var reference = Reference(new()
        {
            ["id"]           = "chunk1",
            ["document_id"]  = "doc1",
            ["title"]        = "CAO GHZ (Versie 4)",
            ["content"]      = "De vakantietoeslag bedraagt 8%.",
            ["heading_path"] = "Hoofdstuk 3 > Artikel 4:10 Vakantietoeslag",
            ["domain_tag"]   = "GHZ",
        });

        var chunk = KnowledgeBaseReferenceMapper.Map([reference]).Single();

        Assert.AreEqual("GHZ", chunk.DomainTag);
        Assert.AreEqual(
            "CAO GHZ (Versie 4) [GHZ]\n\n" +
            "Hoofdstuk 3 > Artikel 4:10 Vakantietoeslag\n\n" +
            "De vakantietoeslag bedraagt 8%.",
            chunk.ToContextText());
    }

    [TestMethod]
    public void ContextText_WithoutIdentityFields_IsJustTheBody()
    {
        // Neighbor-expanded chunks arrive with no title, path or tag - they follow the
        // matched chunk of the same document, and a bare body is the intended shape there.
        var chunk = new RetrievedChunk("id", "doc", 1, 0, Title: null, Summary: null, Content: "Body.");

        Assert.AreEqual("Body.", chunk.ToContextText());
    }

    [TestMethod]
    public void Reference_MissingNativeMetadataFields_AreNull_NotZeroDate()
    {
        var reference = Reference(new() { ["content"] = "text only" });

        var chunks = KnowledgeBaseReferenceMapper.Map([reference]);

        Assert.AreEqual(1, chunks.Count);
        Assert.IsNull(chunks[0].PageCount);
        Assert.IsNull(chunks[0].CreatedAt);
        Assert.IsNull(chunks[0].ModDate);
    }

    [TestMethod]
    public void Reference_MissingOptionalIntFields_DefaultToZero()
    {
        var reference = Reference(new() { ["content"] = "text only" });

        var chunks = KnowledgeBaseReferenceMapper.Map([reference]);

        Assert.AreEqual(1, chunks.Count);
        Assert.AreEqual(0, chunks[0].Page);
        Assert.AreEqual(0, chunks[0].ChunkIndex);
        Assert.AreEqual("", chunks[0].Id);
        Assert.AreEqual("", chunks[0].DocumentId);
        Assert.IsNull(chunks[0].Title);
    }

    [TestMethod]
    public void MultipleReferences_AllValidOnesAreMapped_InOrder()
    {
        var ref1 = Reference(new() { ["id"] = "a", ["content"] = "content A" });
        var ref2 = Reference(new() { ["id"] = "b" }); // no content -> skipped
        var ref3 = Reference(new() { ["id"] = "c", ["content"] = "content C" });

        var chunks = KnowledgeBaseReferenceMapper.Map([ref1, ref2, ref3]);

        CollectionAssert.AreEqual(new[] { "a", "c" }, chunks.Select(c => c.Id).ToList());
    }
}
