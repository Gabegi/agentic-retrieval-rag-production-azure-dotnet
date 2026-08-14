namespace AgenticRagApp.Indexing.Pdf.Services;

// Size class, from the routing measurements (docs/2608/260811/chunking-signals-map.md).
// Picture is decided on density/extraction loss, the other three on estimated tokens.
public enum DocumentSizeClass
{
    // charsPerPage < 1,000 OR bytesPerChar >= 100 - the content likely lives in images.
    Picture,
    // >= 50,000 estimated tokens. Sits in a real corpus gap (~25.1k -> ~90.9k, nothing between).
    Large,
    // >= 4,000 estimated tokens. This line is reasoned, not measured - see Phase D.
    Medium,
    // Below 4,000: small enough that returning the whole document costs about one chunk.
    Small,
}

// Whether the retrieval parent is the whole document or a section within it.
public enum ParentGrain
{
    WholeDocument,
    ParentChild,
}

// The branches of the 260811 strategy recommendation. Every branch is earned; Fallback is
// the default for documents no branch fits.
public enum ChunkingStrategyKind
{
    // Cut at validated heading boundaries - the primary branch. Earned by usable sections.
    HeadingBased,
    // Table-shaped document: tables intact, row-split over the ceiling. Earned by table
    // dominance (TableCharShare).
    TableAware,
    // The whole document is one section, split per block. Earned by being Small (genuinely
    // fits one returned unit) with at least one heading to anchor it.
    SingleSection,
    // The default: no branch fits. Covers picture documents (extraction gate failed,
    // content likely in images - the CU candidates, E6) and structure-less documents too
    // large or too heading-bare to stand as one section. Still chunks whatever text
    // exists; the report row's SizeClass says which kind of Fallback a document is.
    Fallback,
}

// Everything ChunkingStrategySelector.DetermineStrategy learned about one document, plus the
// strategy it picked from that. Carried back to ChunkingService, which dispatches on Strategy
// and records the rest on the document's report row.
// The token ceiling is deliberately NOT part of this decision: over-the-ceiling is a
// per-section question answered by SectionSplitter at cut time, with the section's actual
// block composition in hand. A doc-level preview decided nothing and was removed.
public sealed record ChunkingStrategyDecision(
    DocumentSizeClass    SizeClass,
    ParentGrain          ParentGrain,
    // SectionChecker's verdict: enough headings AND dense enough to divide the text -
    // not the same as HeadingCount > 0.
    bool                 HasUsableSections,
    int                  HeadingCount,
    ChunkingStrategyKind Strategy);
