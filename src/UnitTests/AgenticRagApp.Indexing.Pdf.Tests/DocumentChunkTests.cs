using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using AgenticRagApp.Indexing.Pdf.Models;

namespace RagApp.UnitTests.Indexing;

// Regression coverage for the bug found during the post-Tier-2 audit
// (docs/chunking-rewrite-plan.md): DocumentChunk's rich fields were [JsonIgnore]'d to keep
// them out of the Search upload payload, but that attribute is type-level, not
// call-site-level - it silently stripped the same fields from the ChunkActivity ->
// EmbedAndUploadActivity blob hand-off (chunks.json) and the Stage 2 archive too. Every
// existing test asserted against the in-memory object ChunkDocuments() returns directly,
// so none of them caught it. These two tests exercise the actual serialization boundary.
[TestClass]
public class DocumentChunkTests
{
    private static DocumentChunk FullyPopulated() => new()
    {
        Id                    = "doc1.pdf::0_0",
        DocumentId            = "doc1.pdf",
        Title                 = "Gedragscode medewerkers",
        LastModifiedDate      = DateTimeOffset.Parse("2024-05-01T12:00:00Z"),
        Content               = "Gedragscode medewerkers\n\n_Section: Inleiding_\n\nBody text.",
        Heading               = "_Section: Inleiding_",
        PageNumber            = 0,
        ChunkIndex            = 0,
        ContentVector         = [0.1f, 0.2f, 0.3f],
        Author                = "Contoso P&O",
        CreatedAt             = DateTimeOffset.Parse("2018-02-01T00:00:00Z"),
        ModDate               = DateTimeOffset.Parse("2023-06-15T00:00:00Z"),
        PageCount             = 12,
        Bookmarks             = [new Bookmark("Inleiding", 0, 1, false)],
        Sections              = [new SectionInfo([new SectionSpan(0, 100)], ["/paragraphs/0"], [new SectionElementRef("paragraphs", 0, "Body text.")])],
        Breadcrumb            = "_Section: Inleiding_",
        Structure             = new ChunkStructure(
            Headings:       [new Heading("Inleiding", "sectionHeading", 0, 0)],
            Boilerplate:    [new Heading("Pagina 1 van 12", "pageFooter", 50, 0)],
            Tables:         [new TableInfo(2, 2, [new TableCellInfo(0, 0, "columnHeader", "Naam", null, null)], 10, 0, null, [], [])],
            Dimensions:     new PageDimensions(0, 8.27, 11.69, "inch"),
            SelectionMarks: [new SelectionMarkInfo(0, "selected", 5, 0.98, [new PolygonPoint(1f, 1f)])],
            Figures:        [new FigureInfo("Organogram Contoso", 20, 0, "/figures/0", ["/paragraphs/3"])],
            Lines:          [new LineInfo("Gedragscode medewerkers", 0, 0, [new PolygonPoint(0f, 0f)])]),
    };

    [TestMethod]
    public void DocumentChunk_SurvivesJsonRoundTrip_WithAllFieldsIntact()
    {
        var original = FullyPopulated();

        var json     = JsonSerializer.SerializeToUtf8Bytes(original);
        var restored = JsonSerializer.Deserialize<DocumentChunk>(json)!;

        // Core Search-mapped fields
        Assert.AreEqual(original.Id, restored.Id);
        Assert.AreEqual(original.DocumentId, restored.DocumentId);
        Assert.AreEqual(original.Title, restored.Title);
        Assert.AreEqual(original.LastModifiedDate, restored.LastModifiedDate);
        Assert.AreEqual(original.Content, restored.Content);
        Assert.AreEqual(original.Heading, restored.Heading);
        Assert.AreEqual(original.PageNumber, restored.PageNumber);
        Assert.AreEqual(original.ChunkIndex, restored.ChunkIndex);
        CollectionAssert.AreEqual(original.ContentVector, restored.ContentVector);

        // Everything the chunking rewrite added - the fields the bug lost
        Assert.AreEqual(original.Author, restored.Author);
        Assert.AreEqual(original.CreatedAt, restored.CreatedAt);
        Assert.AreEqual(original.ModDate, restored.ModDate);
        Assert.AreEqual(original.PageCount, restored.PageCount);
        Assert.AreEqual(1, restored.Bookmarks.Count);
        Assert.AreEqual(original.Bookmarks[0].Title, restored.Bookmarks[0].Title);
        Assert.AreEqual(1, restored.Sections.Count);
        Assert.AreEqual(original.Breadcrumb, restored.Breadcrumb);
        Assert.AreEqual(1, restored.Structure.Headings.Count);
        Assert.AreEqual(original.Structure.Headings[0].Content, restored.Structure.Headings[0].Content);
        Assert.AreEqual(1, restored.Structure.Boilerplate.Count);
        Assert.AreEqual(1, restored.Structure.Tables.Count);
        Assert.AreEqual(original.Structure.Tables[0].Cells.Count, restored.Structure.Tables[0].Cells.Count);
        Assert.IsNotNull(restored.Structure.Dimensions);
        Assert.AreEqual(original.Structure.Dimensions!.Width, restored.Structure.Dimensions!.Width);
        Assert.AreEqual(1, restored.Structure.SelectionMarks.Count);
        Assert.AreEqual(1, restored.Structure.Figures.Count);
        Assert.AreEqual(original.Structure.Figures[0].Caption, restored.Structure.Figures[0].Caption);
        Assert.AreEqual(1, restored.Structure.Lines.Count);

        // Derived Tier 2 fields must still be correct after round-tripping - this is
        // exactly what was silently zeroed out by the bug (Tables/Figures reset to
        // defaults -> these computed from nothing).
        Assert.AreEqual(1, restored.TableCount);
        Assert.IsTrue(restored.HasTable);
        CollectionAssert.AreEqual(new[] { "Organogram Contoso" }, restored.FigureCaptions.ToList());
    }

    [TestMethod]
    public void SearchUploadChunk_SerializesToExactlyTheSchemaFields_NoMoreNoLess()
    {
        var upload = SearchUploadChunk.From(FullyPopulated());

        var json = JsonSerializer.Serialize(upload);
        using var doc = JsonDocument.Parse(json);

        var actualKeys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();
        var expectedKeys = new HashSet<string>
        {
            "id", "document_id", "title", "last_modified_date",
            "created_at", "mod_date", "page_count",
            "zenya_document_id", "zenya_version", "zenya_status", "zenya_url",
            "content", "heading",
            "page_number", "chunk_index", "content_vector",
            "table_count", "has_table", "figure_captions",
        };

        CollectionAssert.AreEquivalent(expectedKeys.ToList(), actualKeys.ToList());
    }

    [TestMethod]
    public void SearchUploadChunk_CarriesDerivedFieldValuesCorrectly()
    {
        var upload = SearchUploadChunk.From(FullyPopulated());

        Assert.AreEqual(1, upload.TableCount);
        Assert.IsTrue(upload.HasTable);
        CollectionAssert.AreEqual(new[] { "Organogram Contoso" }, upload.FigureCaptions.ToList());
    }

    [TestMethod]
    public void SearchUploadChunk_CarriesNativeMetadataFieldValuesCorrectly()
    {
        var original = FullyPopulated();
        var upload   = SearchUploadChunk.From(original);

        Assert.AreEqual(original.CreatedAt, upload.CreatedAt);
        Assert.AreEqual(original.ModDate, upload.ModDate);
        Assert.AreEqual(original.PageCount, upload.PageCount);
    }
}
