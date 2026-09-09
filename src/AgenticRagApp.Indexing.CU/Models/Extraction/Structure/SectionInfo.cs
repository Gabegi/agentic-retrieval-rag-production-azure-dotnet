namespace AgenticRagApp.Indexing.CU.Models;

// A service-detected section - Content Understanding's own section tree, flattened: each
// DocumentSection carries one span and a list of element refs, and a nested subsection appears
// as a "/sections/N" ref in its parent.
// - Spans address the markdown, same coordinate system as every other offset in this folder.
//   HeadingChainBuilder reads them for containment (which heading is under which).
// - Elements are the service's own raw JSON-pointer refs ("/paragraphs/15", "/tables/2",
//   "/sections/3"), verbatim. CuOutlineHelper.DepthMap walks them for heading depth.
//
// A ResolvedElements list (each pointer dereferenced to a label like "table 3x4") sat here
// until 2026-09-09. It was DI-era debugging output with no reader outside its own test.
public sealed record SectionInfo(
    IReadOnlyList<SectionSpan> Spans,
    IReadOnlyList<string> Elements);
