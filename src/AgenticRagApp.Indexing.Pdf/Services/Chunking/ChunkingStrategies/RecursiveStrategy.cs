using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Route 2: nothing trustworthy was declared, so compute a hypothesis.
//
// Reached when HeadingSectionGate found no boundary worth honouring. Flat by design: one
// section, N children, no heading machinery at all. It replaces three of the four old routes -
// "table-shaped", "small single section" and "earned nothing" were reporting reasons, never
// separate algorithms.
//
// NOT IMPLEMENTED YET - see step 3 of docs/2608/260818/chunking-service-refactor.md:
//
//   1. blank/whitespace Content -> Empty. Guard Content ONLY: an empty heading list is NORMAL
//      input here, not a defect. That is the class's whole premise.
//   2. prefix = TitleLine(doc.Title, domainTag) - the only context carrier on this route, which
//      is why an empty title is a report signal rather than a curiosity.
//   3. one split call over the whole document, against Max(512 - prefixTokens, 128).
//   4. degenerate constants: SectionIndex 0, running ChildIndex, heading fields null,
//      HeadingSource None, HeadingLocated FALSE, ParentText null. HeadingLocated: true with
//      Source: None was the old FallbackStrategy's contradiction - never reproduce it.
//   5. ChunkingOutcome(units, 0, 0, 0) - zeros mean "not attempted", never "all failed".
//      Reporting doc.Headings.Count against 0 located would fill the >2% heading-location
//      escalation metric with false failures: those headings were not failed, they were
//      deliberately not used. What was discarded belongs on the report row as HeadingCount.
//
// ParentText is never set here, on purpose: this route's "section" is the whole document, so
// materializing it would copy a 90k-char body onto every one of its ~60 children.
public sealed class RecursiveStrategy : IDocumentChunkingStrategy
{
    public string Name => "Recursive";

    public ChunkingOutcome Chunk(PdfExtractionDocument doc, string? domainTag = null) =>
        ChunkingOutcome.Empty;
}
