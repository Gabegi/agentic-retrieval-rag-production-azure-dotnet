using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Step 3 of DetermineStrategy: does the document have USABLE sections - headings that are
// both enough to cut at and actually dividing the text?
//
// A bare count readmits the "large but unstructured" failure the routing model exists to
// express: 2 headings on a 400-page document pass a count check while structuring nothing.
// So the count is paired with the density the profile already measures (B3,
// HeadingsPerThousandChars) - chars as the denominator, not pages, because page count is
// the corpus's weakest size signal (chunking-signals-map.md §4: IGJ Toetsingskader is 5
// pages and the densest document in the corpus).
//
// This class was deleted once for being a logic-free field read and reinstated when the
// density rule gave it a decision to make - the "earns its way back" condition recorded in
// chunking-implementation.md.
public static class SectionChecker
{
    // One boundary cuts nothing: a single heading produces the same single section as none.
    public const int MinHeadings = 2;

    // ~1 heading per 10,000 chars (≈ 4 pages). A tripwire, not a classifier: the corpus
    // averages ~1.0 (one heading per ~1,000 chars - the natural section size), so a floor
    // 10x below that passes every genuinely structured document by a wide margin and exists
    // only to catch the sparse-heading giant. First-pass number - calibrate against the run
    // report like the picker's thresholds.
    public const double MinHeadingsPerThousandChars = 0.1;

    // Null profile: no density measurement exists, so the count alone decides - a missing
    // measurement never punishes a document (the same rule the extraction gate uses).
    public static bool HasUsableSections(int headingCount, DocumentProfile? profile) =>
        headingCount >= MinHeadings &&
        (profile is null || profile.HeadingsPerThousandChars >= MinHeadingsPerThousandChars);
}
