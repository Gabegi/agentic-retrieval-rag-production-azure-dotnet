namespace AgenticRagApp.Indexing.DI.Models;

// One DI section's extent, captured as every Span rather than a single anchor Offset
// (the pattern Heading/TableInfo/FigureInfo use): a section only means something as a
// start-to-end range, so slicing its content the way GetPages slices per-page content
// needs every span, not just the first one.
public sealed record SectionSpan(int Offset, int Length);
