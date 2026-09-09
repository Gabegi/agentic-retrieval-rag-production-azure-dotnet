namespace AgenticRagApp.Indexing.CU.Models;

// Return types the extraction mapper produces. Filled by CUHelper (2026-08-26), which maps the
// typed Content Understanding response (DocumentContent's Paragraphs/Sections/Pages/Tables/
// Figures/Annotations/Hyperlinks) - CU classifies, the helpers map. The markdown-parsing
// predecessor (MarkdownStructureMapper) is gone with it.
//
// - Each record in this folder is one kind of structure the service reports.
// - Every Offset field in this folder (Heading, TableInfo, FigureInfo, AnnotationInfo,
//   HyperlinkInfo) indexes into the document's markdown - the service is asked for utf16 span
//   encoding (hardcoded in the SDK's typed Analyze overload, echo-checked in CUHelper), so these
//   are C# string indices with no conversion. The markdown is VERBATIM (nothing strips or
//   rewrites it), and it is the string chunking cuts - one coordinate system end to end.
//   HeadingLocator slices at these offsets directly (2026-09-09); nothing re-finds text.
// - Offsets are nullable: null means the service provided no span - never 0, since 0 is itself
//   a legitimately valid offset (the very start of the content) and couldn't otherwise be told
//   apart from "no span data".
//
// - No selection marks: Content Understanding has no typed selection marks at all (the checkbox
//   characters survive inline in the markdown). The always-empty slot kept for snapshot compat
//   since 2026-08-26 was removed 2026-09-09 - unknown JSON properties are ignored on read.
// - Page dimensions ride on each PageSpan (PageSpan.Dimensions), not as a parallel list here:
//   a second copy sat in this record until 2026-09-09 and nothing ever read it.
// - Lines are a COUNT, not a list (2026-09-09). The LineInfo list carried every line's text
//   with an always-empty polygon - the geometry that was its whole purpose was dropped by cost
//   (see CuPageHelper.CountLines) - and its only reader was the report's count column.
//
// Raw structural data extracted from one document - not the final chunk metadata.
// - At extraction time, chunk boundaries don't exist yet, so this record does NOT
//   assemble chunks itself.
// - It simply bundles everything the extraction step already produces for free.
// - A later step builds the real ChunkMetadata by matching these items up using
//   their Offset values.
public sealed record PdfDocumentStructure(
    IReadOnlyList<Heading> Headings,               // title / sectionHeading roles only
    IReadOnlyList<Heading> Boilerplate,             // pageHeader / pageFooter / pageNumber roles
    IReadOnlyList<TableInfo> Tables,
    IReadOnlyList<FigureInfo> Figures,
    IReadOnlyList<SectionInfo> Sections,
    IReadOnlyList<AnnotationInfo> Annotations,
    IReadOnlyList<HyperlinkInfo> Hyperlinks,
    // Page-scoped elements mapped 2026-09-08 (A4, A5). Trailing defaults so extraction blobs
    // written before the fields existed still deserialize - null means "extracted before these
    // were mapped", which is not the same as "this document has none". Both are rare by nature
    // (6 barcodes and 36 formulas corpus-wide) and are mapped to be COUNTED - see BarcodeInfo
    // and FormulaInfo for what each measurement is for.
    IReadOnlyList<BarcodeInfo>? Barcodes = null,
    IReadOnlyList<FormulaInfo>? Formulas = null,
    // Text lines the service reported across all pages (DocumentPage.Lines), counted for the
    // file-facts report. Zero on a blob written before the count existed.
    int LineCount = 0)
{
    public static readonly PdfDocumentStructure Empty = new([], [], [], [], [], [], []);
}
