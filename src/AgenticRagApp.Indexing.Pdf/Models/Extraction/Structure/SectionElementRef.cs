namespace AgenticRagApp.Indexing.Pdf.Models;

// One of a SectionInfo's raw JSON-pointer Elements ("/paragraphs/15", "/tables/2", ...),
// dereferenced against the same AnalyzeResult the pointer came from - what
// pre-chunking-action-items.md A1 calls "resolving those refs into actual content."
// - Kind is the pointer's own collection name ("paragraphs", "tables", "figures",
//   "sections"), not remapped to this codebase's own type names, so a ref is traceable
//   back to its raw pointer without a lookup table. An unrecognized pointer shape (a
//   future DI API adding a new element kind) carries the raw pointer string as Kind and
//   Index -1, rather than being silently dropped.
// - Index is the numeric index from the pointer, -1 only for the unrecognized-shape case.
// - Text is a short, human-scannable summary of what's actually there (paragraph content
//   truncated to a label length, "table RxC", figure caption/id, "section N" for a nested
//   section - which is named but not walked, see ResolveSectionElementsHelper's own comment
//   for why). Deliberately a label, not the content itself: this record is carried onto
//   every chunk and serialized with it, so it must not scale with document length. Null
//   means one thing only - the index fell outside the corresponding collection, an
//   inconsistency between DI's own section pointers and its own arrays.
public sealed record SectionElementRef(string Kind, int Index, string? Text);
