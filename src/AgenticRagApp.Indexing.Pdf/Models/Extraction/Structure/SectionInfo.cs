namespace AgenticRagApp.Indexing.Pdf.Models;

// A DI-detected section - the closest thing prebuilt-layout offers to real semantic
// chunk boundaries, as opposed to the page-only boundaries GetPages relies on today.
// Elements are DI's own JSON-pointer refs (e.g. "/paragraphs/15", "/tables/2",
// "/sections/3" for a nested subsection) into whichever paragraphs/tables/figures/
// subsections this section contains. Resolving those refs into actual content/building
// a section tree is left to a future chunk-builder, not done at extraction time.
public sealed record SectionInfo(IReadOnlyList<SectionSpan> Spans, IReadOnlyList<string> Elements);
