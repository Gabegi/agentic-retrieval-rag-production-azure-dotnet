using Azure.AI.ContentUnderstanding;
using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Services;

namespace RagApp.UnitTests.Extraction;

// CUHelper against SDK-shaped fixtures (ContentUnderstandingModelFactory) - the typed-response
// mapping the plan in docs/2608/260826/cuhelper-typed-structure-plan.md describes. Offsets in
// the fixtures are computed with IndexOf against the same markdown the helper maps, never
// hand-counted.
[TestClass]
public class CuHelperTests
{
    // Two pages, a title, two section headings (one nested), furniture on both pages, and a
    // second-page heading whose offset only survives if the furniture strip shifts correctly.
    private const string Md =
        "# Titel Document\n\nIntro tekst.\n\n<!-- PageHeader: TriasWeb logo -->\n\n" +
        "## Sectie Een\n\nInhoud van sectie een.\n\n<!-- PageBreak -->\n\n" +
        "## Sectie Twee\n\nMeer inhoud hier.\n\n<!-- PageFooter: p.2 -->\n";

    private static ContentSpan SpanOf(string text, int length = -1) =>
        ContentUnderstandingModelFactory.ContentSpan(
            Md.IndexOf(text, StringComparison.Ordinal), length < 0 ? text.Length : length);

    private static DocumentParagraph Paragraph(SemanticRole? role, string content, string anchor) =>
        ContentUnderstandingModelFactory.DocumentParagraph(role, content, null, SpanOf(anchor));

    // The standard fixture: paragraphs for title/headings/furniture, two pages whose spans
    // split at the PageBreak marker, and a section tree root -> (sectie een -> sectie twee).
    private static DocumentContent Fixture(
        IEnumerable<DocumentFigure>? figures = null,
        IEnumerable<DocumentTable>? tables = null,
        IEnumerable<DocumentAnnotation>? annotations = null,
        IEnumerable<DocumentHyperlink>? hyperlinks = null)
    {
        var pageTwoStart = Md.IndexOf("## Sectie Twee", StringComparison.Ordinal);

        return ContentUnderstandingModelFactory.DocumentContent(
            markdown: Md,
            startPageNumber: 1,
            endPageNumber: 2,
            pages:
            [
                ContentUnderstandingModelFactory.DocumentPage(
                    pageNumber: 1, width: 8.5f, height: 11f,
                    spans: [ContentUnderstandingModelFactory.ContentSpan(0, pageTwoStart)]),
                ContentUnderstandingModelFactory.DocumentPage(
                    pageNumber: 2, width: 8.5f, height: 11f,
                    spans: [ContentUnderstandingModelFactory.ContentSpan(pageTwoStart, Md.Length - pageTwoStart)]),
            ],
            paragraphs:
            [
                Paragraph(SemanticRole.Title, "Titel Document", "# Titel Document"),        // 0
                Paragraph(null, "Intro tekst.", "Intro tekst."),                            // 1
                Paragraph(SemanticRole.PageHeader, "TriasWeb logo", "<!-- PageHeader"),     // 2
                Paragraph(SemanticRole.SectionHeading, "Sectie Een", "## Sectie Een"),      // 3
                Paragraph(SemanticRole.SectionHeading, "Sectie Twee", "## Sectie Twee"),    // 4
                Paragraph(SemanticRole.PageFooter, "p.2", "<!-- PageFooter"),               // 5
            ],
            sections:
            [
                // Root (unreferenced -> depth 0): the title plus the top-level section.
                ContentUnderstandingModelFactory.DocumentSection(
                    ContentUnderstandingModelFactory.ContentSpan(0, Md.Length),
                    ["/paragraphs/0", "/sections/1"]),
                // Sectie Een (depth 1), containing the nested Sectie Twee.
                ContentUnderstandingModelFactory.DocumentSection(
                    SpanOf("## Sectie Een", Md.Length - Md.IndexOf("## Sectie Een", StringComparison.Ordinal)),
                    ["/paragraphs/3", "/sections/2"]),
                // Sectie Twee (depth 2).
                ContentUnderstandingModelFactory.DocumentSection(
                    SpanOf("## Sectie Twee", 20), ["/paragraphs/4"]),
            ],
            tables: tables,
            figures: figures,
            annotations: annotations,
            hyperlinks: hyperlinks);
    }

    private static CUHelper.MappedDocument Map(DocumentContent document, string encoding = "utf16") =>
        CUHelper.Map(document, encoding);

    // --- Outline --------------------------------------------------------------

    [TestMethod]
    public void TitleComesFromTheTitleRoleParagraph()
    {
        var mapped = Map(Fixture());
        Assert.AreEqual("Titel Document", mapped.Title);
    }

    [TestMethod]
    public void HeadingsCarryCuRoles_NotMarkerCounting()
    {
        var headings = Map(Fixture()).Structure.Headings;

        Assert.AreEqual(3, headings.Count);
        Assert.AreEqual("title", headings[0].Role);
        Assert.AreEqual("sectionHeading", headings[1].Role);
        Assert.AreEqual("Sectie Een", headings[1].Content);
        Assert.AreEqual("Sectie Twee", headings[2].Content);
    }

    [TestMethod]
    public void HeadingContentIsTakenVerbatim_NoMarkerTrimming()
    {
        // No heuristics anywhere (user decision 2026-08-26): paragraph content is mapped
        // verbatim (whitespace-trimmed only). The documented shape is that paragraphs carry
        // the text and the markers live in the markdown rendering; the cu-raw-response
        // capture is where that shape gets verified against reality, not a defensive trim.
        var document = ContentUnderstandingModelFactory.DocumentContent(
            markdown: Md,
            paragraphs: [Paragraph(SemanticRole.SectionHeading, "Sectie Een", "## Sectie Een")]);

        var heading = Map(document).Structure.Headings.Single();
        Assert.AreEqual("Sectie Een", heading.Content);
    }

    [TestMethod]
    public void DepthComesFromSectionTreeNesting()
    {
        var headings = Map(Fixture()).Structure.Headings;

        Assert.AreEqual(1, headings.Single(h => h.Role == "title").Depth);
        Assert.AreEqual(1, headings.Single(h => h.Content == "Sectie Een").Depth);   // root child
        Assert.AreEqual(2, headings.Single(h => h.Content == "Sectie Twee").Depth);  // nested
    }

    [TestMethod]
    public void SectionsAreMappedWithResolvedElementLabels()
    {
        var sections = Map(Fixture()).Structure.Sections;

        Assert.AreEqual(3, sections.Count);
        var root = sections[0];
        Assert.AreEqual("paragraphs", root.ResolvedElements[0].Kind);
        Assert.AreEqual(0, root.ResolvedElements[0].Index);
        Assert.AreEqual("sections", root.ResolvedElements[1].Kind);
    }

    // --- Pages ------------------------------------------------------------------

    [TestMethod]
    public void PageSpansAreCusOwnRangesVerbatim()
    {
        // The model accepts CU's output, not the other way around (user decision 2026-08-26):
        // one PageSpan per reported (page, span) pair, exactly the offsets and lengths the
        // fixture declared - no tiling, no anchoring, no clamping.
        var pageTwoStart = Md.IndexOf("## Sectie Twee", StringComparison.Ordinal);
        var mapped = Map(Fixture());

        Assert.AreEqual(2, mapped.PageSpans.Count);
        Assert.AreEqual((1, 0, pageTwoStart),
            (mapped.PageSpans[0].PageNumber, mapped.PageSpans[0].Offset, mapped.PageSpans[0].Length));
        Assert.AreEqual((2, pageTwoStart, Md.Length - pageTwoStart),
            (mapped.PageSpans[1].PageNumber, mapped.PageSpans[1].Offset, mapped.PageSpans[1].Length));
    }

    [TestMethod]
    public void OffsetsOutsideEveryPageSpan_ReportPageZero_NotAGuess()
    {
        // A page span list with a hole in it: the heading before the first span's start is on
        // no reported page, and the honest answer is 0 ("unknown"), never the nearest page.
        var headingAt = Md.IndexOf("## Sectie Een", StringComparison.Ordinal);
        var document = ContentUnderstandingModelFactory.DocumentContent(
            markdown: Md,
            pages:
            [
                ContentUnderstandingModelFactory.DocumentPage(
                    pageNumber: 1, width: null, height: null,
                    spans: [ContentUnderstandingModelFactory.ContentSpan(headingAt + 50, Md.Length - headingAt - 50)]),
            ],
            paragraphs: [Paragraph(SemanticRole.SectionHeading, "Sectie Een", "## Sectie Een")]);

        var heading = Map(document).Structure.Headings.Single();
        Assert.AreEqual(0, heading.PageNumber);
    }

    [TestMethod]
    public void HeadingsAreAttributedToTheirPages()
    {
        var headings = Map(Fixture()).Structure.Headings;

        Assert.AreEqual(1, headings.Single(h => h.Content == "Sectie Een").PageNumber);
        Assert.AreEqual(2, headings.Single(h => h.Content == "Sectie Twee").PageNumber);
    }

    [TestMethod]
    public void PageDimensionsComeFromTypedPages()
    {
        var mapped = Map(Fixture());

        Assert.AreEqual(2, mapped.Structure.PageDimensions.Count);
        Assert.AreEqual(8.5, mapped.Structure.PageDimensions[0].Width!.Value, 0.001);
        Assert.IsNotNull(mapped.PageSpans[0].Dimensions);
    }

    // --- Furniture --------------------------------------------------------------

    [TestMethod]
    public void BoilerplateComesFromCuRoles_AndMarkdownStaysVerbatim()
    {
        var mapped = Map(Fixture());

        var roles = mapped.Structure.Boilerplate.Select(b => b.Role).ToList();
        CollectionAssert.AreEquivalent(new[] { "pageHeader", "pageFooter" }, roles);
        Assert.AreEqual("TriasWeb logo", mapped.Structure.Boilerplate[0].Content);

        // No heuristics anywhere (user decision 2026-08-26): the markdown is returned exactly
        // as the service produced it - furniture comments included. A stripping pass was built
        // and deleted the same day.
        Assert.AreSame(Md, mapped.Markdown);
    }

    [TestMethod]
    public void HeadingOffsetsAddressTheMarkdownVerbatim()
    {
        var mapped = Map(Fixture());

        foreach (var heading in mapped.Structure.Headings)
        {
            var at = mapped.Markdown.IndexOf(heading.Content, heading.Offset!.Value, StringComparison.Ordinal);
            // The offset lands on the heading's own line: its text begins within the marker's
            // length of the reported anchor.
            Assert.IsTrue(at >= 0 && at - heading.Offset!.Value <= 3,
                $"'{heading.Content}' not at its reported offset {heading.Offset}.");
        }
    }

    // --- Figures / charts / diagrams ---------------------------------------------

    [TestMethod]
    public void FiguresCarryDescriptionAndCaption()
    {
        var figure = ContentUnderstandingModelFactory.DocumentFigure(
            kind: "unknown", id: "1.1", source: null, span: SpanOf("Intro tekst."),
            elements: ["/paragraphs/1"],
            caption: ContentUnderstandingModelFactory.DocumentCaption("Figuur 1", null, null, null),
            footnotes: null, description: "Een organogram.", role: null);

        var mapped = Map(Fixture(figures: [figure])).Structure.Figures.Single();

        Assert.AreEqual("1.1", mapped.Id);
        Assert.AreEqual("Figuur 1", mapped.Caption);
        Assert.AreEqual("Een organogram.", mapped.Description);
        Assert.AreEqual(1, mapped.PageNumber);
    }

    [TestMethod]
    public void ChartPayloadIsJoinedOntoItsFigure_AsOneChartJsConfig()
    {
        var chart = ContentUnderstandingModelFactory.DocumentChartFigure(
            id: "2.1", source: null, span: SpanOf("Meer inhoud hier."), elements: null,
            caption: null, footnotes: null, description: "Omzet per maand.", role: null,
            content: new Dictionary<string, BinaryData>
            {
                ["type"] = BinaryData.FromString("\"bar\""),
                ["data"] = BinaryData.FromString("{\"labels\":[\"jan\"]}"),
            });

        var mapped = Map(Fixture(figures: [chart])).Structure.Figures.Single();

        Assert.AreEqual("chart", mapped.Kind);
        Assert.AreEqual("{\"type\":\"bar\",\"data\":{\"labels\":[\"jan\"]}}", mapped.Payload);
        Assert.AreEqual("Omzet per maand.", mapped.Description);
    }

    [TestMethod]
    public void MermaidPayloadIsJoinedOntoItsFigure()
    {
        var diagram = ContentUnderstandingModelFactory.DocumentMermaidFigure(
            id: "3.1", source: null, span: SpanOf("Inhoud van sectie een."), elements: null,
            caption: null, footnotes: null, description: null, role: null,
            content: "graph TD\n  A --> B");

        var mapped = Map(Fixture(figures: [diagram])).Structure.Figures.Single();

        Assert.AreEqual("mermaid", mapped.Kind);
        Assert.AreEqual("graph TD\n  A --> B", mapped.Payload);
    }

    // --- Tables -------------------------------------------------------------------

    [TestMethod]
    public void TablesCarryCellsMergesCaptionAndFootnotes()
    {
        var table = ContentUnderstandingModelFactory.DocumentTable(
            rowCount: 2, columnCount: 2,
            cells:
            [
                ContentUnderstandingModelFactory.DocumentTableCell(
                    kind: DocumentTableCellKind.ColumnHeader, rowIndex: 0, columnIndex: 0,
                    rowSpan: null, columnSpan: 2, content: "Salaris", source: null, span: null, elements: null),
                ContentUnderstandingModelFactory.DocumentTableCell(
                    kind: null, rowIndex: 1, columnIndex: 0,
                    rowSpan: null, columnSpan: null, content: "2203", source: null, span: null, elements: null),
            ],
            source: null, span: SpanOf("Inhoud van sectie een."),
            caption: ContentUnderstandingModelFactory.DocumentCaption("Tabel 1", null, null, null),
            footnotes: null, role: null);

        var mapped = Map(Fixture(tables: [table])).Structure.Tables.Single();

        Assert.AreEqual(2, mapped.RowCount);
        Assert.AreEqual("Tabel 1", mapped.Caption);
        Assert.AreEqual(2, mapped.Cells[0].ColumnSpan);      // merged header survives
        Assert.AreEqual("columnHeader", mapped.Cells[0].Kind);
        Assert.AreEqual("content", mapped.Cells[1].Kind);    // service default kind
        Assert.AreEqual(1, mapped.PageNumber);
    }

    // --- Annotations / hyperlinks ----------------------------------------------------

    [TestMethod]
    public void AnnotationsCarryAuthorAndCommentThread()
    {
        var annotation = ContentUnderstandingModelFactory.DocumentAnnotation(
            id: "note-1", kind: new DocumentAnnotationKind("comment"),
            spans: [SpanOf("Meer inhoud hier.")], source: null,
            comments:
            [
                ContentUnderstandingModelFactory.DocumentAnnotationComment(
                    "Graag controleren", "Paul", null, null, null),
            ],
            author: "Paul", createdAt: null, lastModifiedAt: null, tags: ["approved"]);

        var mapped = Map(Fixture(annotations: [annotation])).Structure.Annotations.Single();

        Assert.AreEqual("note-1", mapped.Id);
        Assert.AreEqual("Paul", mapped.Author);
        Assert.AreEqual("Paul: Graag controleren", mapped.Comments.Single());
        Assert.AreEqual(2, mapped.PageNumber);
        CollectionAssert.AreEqual(new[] { "approved" }, mapped.Tags.ToList());
    }

    [TestMethod]
    public void HyperlinksCarryTextAndTarget()
    {
        var hyperlink = ContentUnderstandingModelFactory.DocumentHyperlink(
            "Microsoft", "https://www.microsoft.com", SpanOf("Intro tekst."), null);

        var mapped = Map(Fixture(hyperlinks: [hyperlink])).Structure.Hyperlinks.Single();

        Assert.AreEqual("Microsoft", mapped.Content);
        Assert.AreEqual("https://www.microsoft.com", mapped.Uri);
        Assert.AreEqual(1, mapped.PageNumber);
    }

    // --- Map-what's-there-and-warn ------------------------------------------------

    [TestMethod]
    public void MissingTypedCollections_DegradeWithWarnings_NeverThrow()
    {
        var bare = ContentUnderstandingModelFactory.DocumentContent(markdown: "Alleen tekst.");

        var mapped = Map(bare);

        // No typed pages -> NO page spans (not a fabricated full-document one); PageResolver
        // reports the honest (0,0) downstream.
        Assert.AreEqual(0, mapped.PageSpans.Count);
        Assert.AreEqual(0, mapped.Structure.Headings.Count);
        Assert.IsNull(mapped.Title);
        Assert.IsTrue(mapped.Warnings.Any(w => w.Contains("Pages missing")));
        Assert.IsTrue(mapped.Warnings.Any(w => w.Contains("Paragraphs missing")));
    }

    [TestMethod]
    public void NonUtf16Encoding_Warns()
    {
        var mapped = Map(Fixture(), encoding: "codePoint");
        Assert.IsTrue(mapped.Warnings.Any(w => w.Contains("utf16")));
    }

    [TestMethod]
    public void EmptyMarkdown_ReturnsEmptyStructureWithWarning()
    {
        var mapped = Map(ContentUnderstandingModelFactory.DocumentContent(markdown: ""));

        Assert.AreEqual("", mapped.Markdown);
        Assert.AreEqual(0, mapped.PageSpans.Count);
        Assert.IsTrue(mapped.Warnings.Any(w => w.Contains("empty markdown")));
    }
}
