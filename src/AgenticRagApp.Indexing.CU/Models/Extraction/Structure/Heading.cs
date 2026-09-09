namespace AgenticRagApp.Indexing.CU.Models;

// A single heading/boilerplate paragraph detected in the PDF:
// - PageNumber = which page the paragraph is on, for display/debugging only.
//   It can't be used for ordering, because two on the same page look identical by page number.
// - Depth = the heading level the service itself rendered: the run of "#" at Offset ("#"=1,
//   "##"=2, ...; not capped - the corpus carries 7-hash headings). Read once at extraction time
//   by CuOutlineHelper (2026-09-09), which also verifies the span starts at that marker; 0 when
//   it does not (warned per document). Until 2026-09-09 it was derived from the section tree
//   nesting, which disagreed with the rendered level on 1,189 of 2,451 headings.
// - Content is the paragraph text VERBATIM, without the "#" marker (that lives in the markdown);
//   Offset is the paragraph span, which STARTS at the marker - HeadingLocator cuts there.
// - Boilerplate paragraphs (pageHeader/pageFooter/pageNumber) reuse this record with Depth 1,
//   an unread default, never a real "top-level" claim.
public sealed record Heading(string Content, string Role, int? Offset, int PageNumber, int Depth = 1);
