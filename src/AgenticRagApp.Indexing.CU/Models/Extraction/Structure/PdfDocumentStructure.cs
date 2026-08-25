namespace AgenticRagApp.Indexing.CU.Models;

// Return types the extraction mapper (CuStructureMapper) produces:
// - Each record in this folder is one kind of structure the service reports.
// - Every Offset field in this folder (Heading, TableInfo, SelectionMarkInfo, FigureInfo,
//   LineInfo) indexes into analysis.Content / RawContent. Because
//   the service returns markdown, that string IS the markdown-rendered content, not plain
//   text - every span is computed against it. So these offsets are markdown-relative, and
//   they address the RAW markdown, not the cleaned Content that CuMarkdownPager assembles.
//   See PageSpan for the two coordinate systems and HeadingLocator for how they are bridged.
// - Heading/TableInfo/FigureInfo/LineInfo's Offset is nullable: it's an anchor into
//   the first span only, and when the service didn't provide one, null means
//   "unknown" - never 0, since 0 is itself a legitimately valid offset (the very start
//   of the content) and couldn't otherwise be told apart from "no span data".
//
// - Selection marks and lines are always empty: Content Understanding has no typed selection
//   marks (the checkbox characters survive inline in the markdown instead), and encodes
//   geometry as an opaque source string rather than polygons.
//
// Raw structural data extracted from one document - not the final chunk metadata.
// - At extraction time, chunk boundaries don't exist yet, so this record does NOT
//   assemble chunks itself.
// - It simply bundles everything the extraction step already produces for free.
// - A later step builds the real ChunkMetadata by matching these items up using
//   their Offset values.
// - Selection marks and lines are always empty: Content Understanding has no typed selection
//   marks, and encodes geometry as an opaque source string rather than polygons.
public sealed record PdfDocumentStructure(
    IReadOnlyList<Heading> Headings,               // title / sectionHeading roles only
    IReadOnlyList<Heading> Boilerplate,             // pageHeader / pageFooter / footnote / pageNumber roles
    IReadOnlyList<TableInfo> Tables,
    IReadOnlyList<PageDimensions> PageDimensions,
    IReadOnlyList<SelectionMarkInfo> SelectionMarks,
    IReadOnlyList<FigureInfo> Figures,
    IReadOnlyList<LineInfo> Lines,
    IReadOnlyList<SectionInfo> Sections);
