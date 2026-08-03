namespace AgenticRagApp.Indexing.Pdf.Models;

// A single heading/boilerplate paragraph detected in the PDF:
// - PageNumber = which page the paragraph is on, for display/debugging only.
//   It can't be used for ordering, because two on the same page look identical by page number.
public sealed record Heading(string Content, string Role, int? Offset, int PageNumber);
