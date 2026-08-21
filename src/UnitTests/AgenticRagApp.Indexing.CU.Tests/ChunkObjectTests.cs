using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using AgenticRagApp.Indexing.CU.Models;

namespace RagApp.UnitTests.Indexing;

// Ported from DocumentChunkTests when DocumentChunk was folded into ChunkObject. The three
// regressions guarded here are properties of the PIPELINE, not of the old type, so they
// outlived it:
//
//   1. [JsonIgnore] is type-level, not call-site-level. The rich fields were ignored to keep
//      them out of the Search upload payload, and that silently stripped them from the
//      ChunkActivity -> EmbedAndUploadActivity blob hand-off too. ChunkObject is MORE exposed
//      to this than DocumentChunk was, not less: nearly every field it presents is a
//      [JsonIgnore] pass-through onto Metadata, so the round trip has to be asserted through
//      the real serializer rather than against the in-memory object.
//   2. The Search schema is a fixed key set. Anything added or dropped without the index
//      definition moving with it fails at upload, per document, at the end of a long run.
//   3. The OOM of 260812 - per-DOCUMENT data attached to a per-CHUNK record reached 772 MB of
//      hand-off blob against a 16 MB extraction artifact. Asserted as a size ceiling rather
//      than a deny-list, because the next instance will be a field nobody has written yet.
[TestClass]
public class ChunkObjectTests
{
    private static ChunkObject FullyPopulated() => new()
    {
        Content      = "Body text of the section, long enough to be a real cut.",
        Start        = 120,
        Length       = 55,
        SectionIndex = 0,
        ChildIndex   = 0,

        HeadingText    = "Inleiding",
        HeadingPath    = "Hoofdstuk 1 > Inleiding",
        HeadingDepth   = 2,
        HeadingSource  = ChunkHeadingSource.DiHeading,
        HeadingLocated = true,

        ContentVector = [0.1f, 0.2f, 0.3f],

        Metadata = new ChunkMetadata
        {
            Id               = "doc1.pdf::0_0",
            DocumentId       = "doc1.pdf",
            SectionId        = "doc1.pdf::0",
            Title            = "Gedragscode medewerkers",
            Language         = "nl",
            Author           = "Contoso P&O",
            LastModifiedDate = DateTimeOffset.Parse("2024-05-01T12:00:00Z"),
            CreatedAt        = DateTimeOffset.Parse("2018-02-01T00:00:00Z"),
            ModDate          = DateTimeOffset.Parse("2023-06-15T00:00:00Z"),
            PageCount        = 12,
            Breadcrumb       = "Inleiding",
            PageStart        = 1,
            PageEnd          = 1,
            Prefix           = "Gedragscode medewerkers [GHZ]\n\nHoofdstuk 1 > Inleiding",
            TokenCount       = 24,
            FamilyId         = "fam-1",
            DomainTag        = "GHZ",
            Route            = "DeclaredBoundary",
            SizeClass        = "Medium",
            TableCount       = 1,
            FigureCaptions   = ["Organogram Contoso"],
            Structure        = new ChunkStructure(
                Headings:       [new Heading("Inleiding", "sectionHeading", 0, 0)],
                Boilerplate:    [new Heading("Pagina 1 van 12", "pageFooter", 50, 0)],
                Tables:         [new TableInfo(2, 2, [new TableCellInfo(0, 0, "columnHeader", "Naam", null, null)], 10, 0, null, [], [])],
                Dimensions:     new PageDimensions(0, 8.27, 11.69, "inch"),
                SelectionMarks: [new SelectionMarkInfo(0, "selected", 5, 0.98, [new PolygonPoint(1f, 1f)])],
                Figures:        [new FigureInfo("Organogram Contoso", 20, 0, "/figures/0", ["/paragraphs/3"])]),
        },
    };

    [TestMethod]
    public void ChunkObject_SurvivesJsonRoundTrip_WithAllFieldsIntact()
    {
        var original = FullyPopulated();

        var json     = JsonSerializer.SerializeToUtf8Bytes(original);
        var restored = JsonSerializer.Deserialize<ChunkObject>(json)!;

        // The cut - written by a strategy in step 3.
        Assert.AreEqual(original.Content, restored.Content);
        Assert.AreEqual(original.Start, restored.Start);
        Assert.AreEqual(original.Length, restored.Length);
        Assert.AreEqual(original.SectionIndex, restored.SectionIndex);
        Assert.AreEqual(original.ChildIndex, restored.ChildIndex);
        Assert.AreEqual(original.HeadingText, restored.HeadingText);
        Assert.AreEqual(original.HeadingPath, restored.HeadingPath);
        Assert.AreEqual(original.HeadingDepth, restored.HeadingDepth);
        Assert.AreEqual(original.HeadingSource, restored.HeadingSource);
        Assert.AreEqual(original.HeadingLocated, restored.HeadingLocated);
        CollectionAssert.AreEqual(original.ContentVector, restored.ContentVector);

        // The metadata - written by ChunkMetadataBuilder in step 4. Reached through the
        // [JsonIgnore] pass-throughs on purpose: that is the surface the bug was on.
        Assert.AreEqual(original.Id, restored.Id);
        Assert.AreEqual(original.DocumentId, restored.DocumentId);
        Assert.AreEqual(original.SectionId, restored.SectionId);
        Assert.AreEqual(original.Title, restored.Title);
        Assert.AreEqual(original.Language, restored.Language);
        Assert.AreEqual(original.LastModifiedDate, restored.LastModifiedDate);
        Assert.AreEqual(original.CreatedAt, restored.CreatedAt);
        Assert.AreEqual(original.ModDate, restored.ModDate);
        Assert.AreEqual(original.PageCount, restored.PageCount);
        Assert.AreEqual(original.PageStart, restored.PageStart);
        Assert.AreEqual(original.PageEnd, restored.PageEnd);
        Assert.AreEqual(original.TokenCount, restored.TokenCount);
        Assert.AreEqual(original.FamilyId, restored.FamilyId);
        Assert.AreEqual(original.DomainTag, restored.DomainTag);
        Assert.AreEqual(original.Metadata.Breadcrumb, restored.Metadata.Breadcrumb);

        // Prefix travels because ContentHash is computed FROM it - a restore that rebuilt a
        // chunk without it would resolve a different vector than the one it just cached.
        Assert.AreEqual(original.Prefix, restored.Prefix);
        Assert.AreEqual(original.EmbeddingText, restored.EmbeddingText);
        Assert.AreEqual(original.ContentHash, restored.ContentHash);

        Assert.AreEqual(1, restored.Metadata.Structure.Headings.Count);
        Assert.AreEqual(1, restored.Metadata.Structure.Boilerplate.Count);
        Assert.IsNotNull(restored.Metadata.Structure.Dimensions);
        Assert.AreEqual(1, restored.Metadata.Structure.SelectionMarks.Count);
        Assert.AreEqual(1, restored.Metadata.Structure.Tables.Count);
        Assert.AreEqual(
            original.Metadata.Structure.Tables[0].Cells.Count,
            restored.Metadata.Structure.Tables[0].Cells.Count);
        Assert.AreEqual(1, restored.Metadata.Structure.Figures.Count);

        // Stamped in step 4 rather than recomputed on read, precisely so they survive this.
        Assert.AreEqual(1, restored.TableCount);
        CollectionAssert.AreEqual(new[] { "Organogram Contoso" }, restored.FigureCaptions.ToList());
    }

    [TestMethod]
    public void SearchUploadChunk_SerializesToExactlyTheSchemaFields_NoMoreNoLess()
    {
        var upload = SearchUploadChunk.From(FullyPopulated());

        var json = JsonSerializer.Serialize(upload);
        using var doc = JsonDocument.Parse(json);

        var actualKeys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();

        // Mirrors IndexService.BuildIndexDefinition exactly. The CSV-era fields (summary,
        // department, quick_code, relative_path, check_date) are deliberately absent - PDF and
        // CSV no longer share an index (action-plan.md B2).
        var expectedKeys = new HashSet<string>
        {
            // identity and position: *_id names a thing, *_index names a position within a
            // stated scope.
            "id", "document_id", "section_id", "section_index", "child_index", "grain",
            // content and heading context
            "title", "content", "parent_text",
            "heading_text", "heading_path", "heading_depth", "heading_source",
            // where the cut sits in the source, so a structural window can slice it
            "chunk_start", "chunk_length",
            // document metadata
            "last_modified_date", "created_at", "mod_date", "page_count",
            "zenya_document_id", "zenya_version", "zenya_status", "zenya_url",
            // which route ran and how the document was sized
            "route_name", "size_class",
            // validity, parsed out of the title
            "valid_from", "valid_to", "version",
            // pages - a unit can span them once sections are the grain
            "page_start", "page_end",
            // size - neither reconstructs the other, chars/token is not constant
            "char_count", "token_count",
            // identity / ambiguity - the only deterministic fix for wrong-sector answers
            "family_id", "domain_tag", "confusable_with", "population", "language",
            "content_vector",
            "table_count", "has_table", "figure_captions",
            // quality flags
            "is_overlap", "heading_located", "page_extraction_flag",
        };

        CollectionAssert.AreEquivalent(expectedKeys.ToList(), actualKeys.ToList());
    }

    [TestMethod]
    public void SearchUploadChunk_CarriesDerivedFieldValuesCorrectly()
    {
        var upload = SearchUploadChunk.From(FullyPopulated());

        Assert.AreEqual(1, upload.TableCount);
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

    // Regression guard for the OOM of 260812 - see the class comment. A cap, not a deny-list.
    [TestMethod]
    public void ChunkObject_SerializedSize_StaysProportionalToItsOwnContent()
    {
        var chunk = FullyPopulated();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(chunk).Length;

        // Generous: this fixture is a ~55-character body carrying a table, a figure and a
        // vector, so it is already unrepresentatively heavy per character. The point is the
        // order of magnitude - the real leak made chunks ~253 KB each.
        Assert.IsTrue(bytes < 8_000,
            $"a single chunk serialized to {bytes} bytes - check nothing per-DOCUMENT was " +
            "attached to this per-CHUNK record (see ChunkStructure's comment)");
    }
}
