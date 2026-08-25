using Azure.AI.ContentUnderstanding;
using AgenticRagApp.Indexing.CU.Services;

namespace RagApp.UnitTests.PdfExtraction;

// The typed structure the chunking layer routes and builds metadata on. Headings matter most:
// ChunkingService picks DeclaredBoundaryStrategy over RecursiveStrategy on heading count and
// density, so a mapping that quietly produces none changes how the entire corpus is chunked.
[TestClass]
public class CuStructureMapperTests
{
    private static DocumentParagraph Paragraph(SemanticRole? role, string content, int offset) =>
        ContentUnderstandingModelFactory.DocumentParagraph(
            role: role, content: content, source: null,
            span: ContentUnderstandingModelFactory.ContentSpan(offset, content.Length));

    private static DocumentPage Page(int number, int offset, int length) =>
        ContentUnderstandingModelFactory.DocumentPage(
            pageNumber: number, width: 8.5f, height: 11f,
            spans: [ContentUnderstandingModelFactory.ContentSpan(offset, length)],
            angle: 0f, words: [], lines: [], barcodes: [], formulas: []);

    private static DocumentContent Document(
        string markdown,
        IEnumerable<DocumentParagraph>? paragraphs = null,
        IEnumerable<DocumentFigure>? figures = null,
        IEnumerable<DocumentTable>? tables = null,
        IEnumerable<DocumentSection>? sections = null,
        IEnumerable<DocumentPage>? pages = null) =>
        ContentUnderstandingModelFactory.DocumentContent(
            mimeType: "application/pdf", analyzerId: "cap-pdf-layout", category: null, path: null,
            markdown: markdown, fields: null, startPageNumber: 1, endPageNumber: 1,
            unit: LengthUnit.Inch,
            pages: pages ?? [Page(1, 0, markdown.Length)],
            paragraphs: paragraphs ?? [], sections: sections ?? [], tables: tables ?? [],
            figures: figures ?? [], annotations: [], hyperlinks: [], segments: []);

    [TestMethod]
    public void HeadingRoles_BecomeHeadings_AndFurnitureBecomesBoilerplate()
    {
        var markdown = "# Beleid\n\n## Artikel 1\n\nContoso beleid\n\npagina 1";

        var structure = CuStructureMapper.Map(Document(markdown, paragraphs:
        [
            Paragraph(SemanticRole.Title,         "Beleid",        markdown.IndexOf("Beleid")),
            Paragraph(SemanticRole.SectionHeading, "Artikel 1",    markdown.IndexOf("Artikel 1")),
            Paragraph(SemanticRole.PageHeader,     "Contoso beleid", markdown.IndexOf("Contoso beleid")),
            Paragraph(SemanticRole.PageNumber,     "pagina 1",     markdown.IndexOf("pagina 1")),
        ]), []);

        CollectionAssert.AreEqual(
            new[] { "Beleid", "Artikel 1" },
            structure.Headings.Select(h => h.Content).ToList());
        CollectionAssert.AreEqual(
            new[] { "Contoso beleid", "pagina 1" },
            structure.Boilerplate.Select(b => b.Content).ToList());
    }

    [TestMethod]
    public void RoleNames_KeepTheirExistingCamelCaseSpelling()
    {
        // These strings are already in the index and in every stored chunk. Taking the CU enum's
        // ToString() would silently rename "sectionHeading" to "SectionHeading" mid-corpus.
        var markdown = "## Artikel 1";

        var structure = CuStructureMapper.Map(Document(markdown, paragraphs:
        [
            Paragraph(SemanticRole.SectionHeading, "Artikel 1", markdown.IndexOf("Artikel 1")),
        ]), []);

        Assert.AreEqual("sectionHeading", structure.Headings[0].Role);
    }

    [TestMethod]
    public void HeadingDepth_ComesFromTheMarkdownMarkerRun()
    {
        // There is no structured heading-level field in the response - the "#" run immediately
        // before the paragraph's span offset is the only depth signal the service gives.
        var markdown = "# Een\n\n### Drie\n\n###### Zes";

        var structure = CuStructureMapper.Map(Document(markdown, paragraphs:
        [
            Paragraph(SemanticRole.SectionHeading, "Een",  markdown.IndexOf("Een")),
            Paragraph(SemanticRole.SectionHeading, "Drie", markdown.IndexOf("Drie")),
            Paragraph(SemanticRole.SectionHeading, "Zes",  markdown.IndexOf("Zes")),
        ]), []);

        CollectionAssert.AreEqual(new[] { 1, 3, 6 }, structure.Headings.Select(h => h.Depth).ToList());
    }

    [TestMethod]
    public void TitleRole_IsAlwaysDepthOne()
    {
        // A Title is rendered setext ("Text\n===" ), not ATX, so scanning for a "#" run would
        // find whatever happened to precede it.
        var markdown = "Beleidsdocument\n===============";

        var structure = CuStructureMapper.Map(Document(markdown, paragraphs:
        [
            Paragraph(SemanticRole.Title, "Beleidsdocument", 0),
        ]), []);

        Assert.AreEqual(1, structure.Headings[0].Depth);
    }

    [TestMethod]
    public void HeadingWithNoMarkerRun_FallsBackToDepthOne()
    {
        // A bare numbered TOC entry can carry the SectionHeading role without being rendered as
        // ATX at all. Guessing a depth is fine; throwing is not.
        var markdown = "1.\n\n2.";

        var structure = CuStructureMapper.Map(Document(markdown, paragraphs:
        [
            Paragraph(SemanticRole.SectionHeading, "1.", 0),
        ]), []);

        Assert.AreEqual(1, structure.Headings[0].Depth);
    }

    [TestMethod]
    public void Figures_CarryTheirGeneratedDescription()
    {
        // The reason for the migration. Document Intelligence had no equivalent, so an
        // uncaptioned figure was deleted outright rather than indexed.
        var figure = ContentUnderstandingModelFactory.DocumentFigure(
            kind: "unknown", id: "1.1", source: null,
            span: ContentUnderstandingModelFactory.ContentSpan(0, 5),
            elements: [], caption: null, footnotes: [],
            description: "Een stroomdiagram met vier stappen.", role: null);

        var structure = CuStructureMapper.Map(Document("![ ](figures/1.1)", figures: [figure]), []);

        Assert.AreEqual(1, structure.Figures.Count);
        Assert.AreEqual("Een stroomdiagram met vier stappen.", structure.Figures[0].Description);
        Assert.AreEqual("1.1", structure.Figures[0].Id);
    }

    [TestMethod]
    public void Tables_CarryCellsWithMergedSpans()
    {
        // The markdown table format cannot express merged cells, so the typed cells are the only
        // remaining record of the true grid - see the verifier's tableFormat note.
        var cell = ContentUnderstandingModelFactory.DocumentTableCell(
            kind: DocumentTableCellKind.ColumnHeader, rowIndex: 0, columnIndex: 0,
            rowSpan: 2, columnSpan: 3, content: "Salaris", source: null,
            span: ContentUnderstandingModelFactory.ContentSpan(0, 7), elements: []);

        var table = ContentUnderstandingModelFactory.DocumentTable(
            rowCount: 2, columnCount: 3, cells: [cell], source: null,
            span: ContentUnderstandingModelFactory.ContentSpan(0, 20),
            caption: null, footnotes: [], role: null);

        var structure = CuStructureMapper.Map(Document("| Salaris |", tables: [table]), []);

        Assert.AreEqual(1, structure.Tables.Count);
        Assert.AreEqual(2, structure.Tables[0].RowCount);
        Assert.AreEqual(2, structure.Tables[0].Cells[0].RowSpan);
        Assert.AreEqual(3, structure.Tables[0].Cells[0].ColumnSpan);
    }

    [TestMethod]
    public void Sections_CarrySpansAndRawElementPointers()
    {
        var section = ContentUnderstandingModelFactory.DocumentSection(
            span: ContentUnderstandingModelFactory.ContentSpan(0, 42),
            elements: ["/paragraphs/0", "/tables/1"]);

        var structure = CuStructureMapper.Map(Document("Inhoud", sections: [section]), []);

        Assert.AreEqual(1, structure.Sections.Count);
        Assert.AreEqual(0,  structure.Sections[0].Spans[0].Offset);
        Assert.AreEqual(42, structure.Sections[0].Spans[0].Length);
        CollectionAssert.AreEqual(new[] { "/paragraphs/0", "/tables/1" }, structure.Sections[0].Elements.ToList());
    }

    [TestMethod]
    public void SelectionMarksAndLines_AreEmptyRatherThanWrong()
    {
        // Content Understanding has no typed selection marks at all, and encodes geometry as an
        // opaque source string rather than polygons. The ☒/☐ characters survive in the markdown,
        // so the content is not lost - only the typed lists are.
        var structure = CuStructureMapper.Map(Document("☒ Akkoord"), []);

        Assert.AreEqual(0, structure.SelectionMarks.Count);
        Assert.AreEqual(0, structure.Lines.Count);
    }

    [TestMethod]
    public void PageNumbers_ResolveFromTheOffsetsOwnPage()
    {
        var markdown = "Pagina een.\nPagina twee.";
        var pages = new[] { Page(1, 0, 12), Page(2, 12, markdown.Length - 12) };

        var structure = CuStructureMapper.Map(Document(markdown, paragraphs:
        [
            Paragraph(SemanticRole.SectionHeading, "Pagina een.",  0),
            Paragraph(SemanticRole.SectionHeading, "Pagina twee.", 12),
        ], pages: pages), []);

        Assert.AreEqual(1, structure.Headings[0].PageNumber);
        Assert.AreEqual(2, structure.Headings[1].PageNumber);
    }
}
