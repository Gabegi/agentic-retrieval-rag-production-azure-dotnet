namespace AgenticRagApp.Indexing.CU.Models;

// Return types the extraction mapper produces. Filled by CUHelper (2026-08-26), which maps the
// typed Content Understanding response (DocumentContent's Paragraphs/Sections/Pages/Tables/
// Figures/Annotations/Hyperlinks) - CU classifies, the helpers map. The markdown-parsing
// predecessor (MarkdownStructureMapper) is gone with it.
//
// - Each record in this folder is one kind of structure the service reports.
// - Every Offset field in this folder (Heading, TableInfo, FigureInfo, LineInfo,
//   AnnotationInfo, HyperlinkInfo) indexes into the document's markdown - the service is asked
//   for utf16 span encoding (hardcoded in the SDK's typed Analyze overload, echo-checked in
//   CUHelper), so these are C# string indices with no conversion. The markdown is VERBATIM
//   (nothing strips or rewrites it), and it is the string chunking cuts - one coordinate
//   system end to end.
// - Offsets are nullable: null means the service provided no span - never 0, since 0 is itself
//   a legitimately valid offset (the very start of the content) and couldn't otherwise be told
//   apart from "no span data".
//
// - Selection marks are always empty: Content Understanding has no typed selection marks at all
//   (the checkbox characters survive inline in the markdown instead) - dropped by decision
//   2026-08-26, the slot kept only for serialized-snapshot compatibility.
//
// Raw structural data extracted from one document - not the final chunk metadata.
// - At extraction time, chunk boundaries don't exist yet, so this record does NOT
//   assemble chunks itself.
// - It simply bundles everything the extraction step already produces for free.
// - A later step builds the real ChunkMetadata by matching these items up using
//   their Offset values.
public sealed record PdfDocumentStructure(
    IReadOnlyList<Heading> Headings,               // title / sectionHeading roles only
    IReadOnlyList<Heading> Boilerplate,             // pageHeader / pageFooter / footnote / pageNumber roles
    IReadOnlyList<TableInfo> Tables,
    IReadOnlyList<PageDimensions> PageDimensions,
    IReadOnlyList<SelectionMarkInfo> SelectionMarks,
    IReadOnlyList<FigureInfo> Figures,
    IReadOnlyList<LineInfo> Lines,
    IReadOnlyList<SectionInfo> Sections,
    IReadOnlyList<AnnotationInfo> Annotations,
    IReadOnlyList<HyperlinkInfo> Hyperlinks)
{
    public static readonly PdfDocumentStructure Empty = new([], [], [], [], [], [], [], [], [], []);
}
