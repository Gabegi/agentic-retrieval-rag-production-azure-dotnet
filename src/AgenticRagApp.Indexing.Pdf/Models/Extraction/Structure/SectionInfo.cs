namespace AgenticRagApp.Indexing.Pdf.Models;

// A DI-detected section - the closest thing prebuilt-layout offers to real semantic
// chunk boundaries, as opposed to the page-only boundaries GetPages relies on today.
// - Elements are DI's own raw JSON-pointer refs (e.g. "/paragraphs/15", "/tables/2",
//   "/sections/3" for a nested subsection) into whichever paragraphs/tables/figures/
//   subsections this section contains - kept verbatim for traceability back to the DI
//   response.
// - ResolvedElements is the same list, dereferenced against the AnalyzeResult they came
//   from (GetSectionsHelper -> ResolveSectionElementsHelper) - what each pointer actually
//   points at, computed once at extraction time rather than left as a future chunk-builder's
//   job. Building a full section *tree* (nested subsections walked recursively) is still not
//   done here - see ResolveSectionElementsHelper's own comment on why nested "/sections/N"
//   refs are left as a bare reference rather than walked.
public sealed record SectionInfo(
    IReadOnlyList<SectionSpan> Spans,
    IReadOnlyList<string> Elements,
    IReadOnlyList<SectionElementRef> ResolvedElements);
