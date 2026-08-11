namespace AgenticRagApp.Indexing.Pdf.Models;

// A single heading/boilerplate paragraph detected in the PDF:
// - PageNumber = which page the paragraph is on, for display/debugging only.
//   It can't be used for ordering, because two on the same page look identical by page number.
// - Depth = the H1-H6 markdown nesting level DI itself rendered ("#"=1 .. "######"=6), read
//   off the raw content once at extraction time (see GetHeadingsHelper.ComputeDepth) rather
//   than re-derived by hand later, as every prior heading-depth analysis had to do. Defaults
//   to 1 for boilerplate paragraphs (pageHeader/pageFooter/footnote/pageNumber), which reuse
//   this same record but have no nesting concept - 1 is a safe, unread default there, never
//   a real "top-level" claim.
public sealed record Heading(string Content, string Role, int? Offset, int PageNumber, int Depth = 1);
