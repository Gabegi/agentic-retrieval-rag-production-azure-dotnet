namespace AgenticRagApp.Indexing.CU.Models;

// Return types used by PdfDocumentIntelligenceAnalyzer's Get* methods:
// - Each record in this folder matches one Get* method one-to-one.
// - This keeps callers focused only on the fields they actually asked for.
// - Every Offset field in this folder (Heading, TableInfo, SelectionMarkInfo, FigureInfo,
//   LineInfo) indexes into analysis.Content / RawContent. Because
//   AnalyzeDocumentAsync requests OutputContentFormat.Markdown, that string IS the
//   markdown-rendered content, not plain text - DI recomputes every span against
//   whichever format was requested, so this isn't an edge case to guard against, it's
//   how these offsets work now. A future ChunkMetadata builder must match content
//   against these markdown-relative offsets, not plain-text ones.
// - Heading/TableInfo/FigureInfo/LineInfo's Offset is nullable: it's an anchor into
//   the first Span/BoundingRegion only, and when DI didn't provide one, null means
//   "unknown" - never 0, since 0 is itself a legitimately valid offset (the very start
//   of the content) and couldn't otherwise be told apart from "no span data". Selection
//   marks don't have this ambiguity (DI always gives exactly one Span per mark).
//
// Raw structural data extracted from one PDF - not the final chunk metadata.
// - At extraction time, chunk boundaries don't exist yet, so this record does NOT
//   assemble chunks itself.
// - It simply bundles everything the extraction step already produces for free.
// - A later step builds the real ChunkMetadata by matching these items up using
//   their Offset values.
// - NativeMetadata/Bookmarks live once, at the top level of PdfExtractionResult -
//   not duplicated in here.
public sealed record PdfDocumentStructure(
    IReadOnlyList<Heading> Headings,               // title / sectionHeading roles only
    IReadOnlyList<Heading> Boilerplate,             // pageHeader / pageFooter / footnote / pageNumber roles
    IReadOnlyList<TableInfo> Tables,
    IReadOnlyList<PageDimensions> PageDimensions,
    IReadOnlyList<SelectionMarkInfo> SelectionMarks,
    IReadOnlyList<FigureInfo> Figures,
    IReadOnlyList<LineInfo> Lines,
    IReadOnlyList<SectionInfo> Sections);
