using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Which of the two chunking routes a document takes.
//
// Named for the algorithm that runs, never for the reason it was chosen: the reasons live on
// the report row (SizeClass, HeadingCount, HasUsableSections), so a label can no longer drift
// from the behaviour the way the four-value ChunkingStrategyKind did - four routes, one shared
// cascade. See docs/2608/260818/chunking-service-refactor.md step 2.
public enum ChunkingRoute
{
    // The document declared enough boundary to honour, so its headings become the section
    // boundaries and every chunk arrives with its heading chain attached.
    DeclaredBoundary,

    // Nothing trustworthy was declared. The body is cut flat against the ceiling, and the
    // document title is the only context a chunk carries.
    Recursive,
}

// What the gate read and what it decided. All four values go on the report row: with two
// routes the label alone no longer says why, so the signals travel with it.
public sealed record SectionGateVerdict(
    DocumentSizeClass SizeClass,
    int               HeadingCount,
    bool              HasUsableSections,
    ChunkingRoute     Route);

// Step 2 of the chunking stage: read the document's declared structure and decide the route.
//
// Reads only counts and the profile's measurements - never the text. LOCATING those headings
// in the cleaned text (string match, page-windowed) is DeclaredBoundaryStrategy's job and
// happens after this, because route 2 never anchors anything and the formula below needs no
// positions.
//
// The formula, and nothing beyond it:
//
//   (headings >= 2 AND headings per 1,000 chars >= 0.1)   -> DeclaredBoundary
//   OR (SizeClass == Small AND headings >= 1)             -> DeclaredBoundary
//   otherwise                                            -> Recursive
//
// The first clause is SectionChecker's "did the document declare enough boundary to honour"
// (one heading in 30k chars is a label, not a structure). The second is the N=1 admission: a
// Small document fits in one unit, so its single heading genuinely describes the whole thing -
// which is why SingleSection never needed a class of its own.
//
// The gate runs ONCE per document, here. Strategies never re-check it, and being over the
// token ceiling never changes a route - it only triggers a cut inside one.
public static class HeadingSectionGate
{
    // The N=1 admission's floor. Zero headings on a Small document declares nothing at all.
    public const int MinHeadingsWhenSmall = 1;

    public static SectionGateVerdict Read(PdfExtractionDocument doc)
    {
        var sizeClass    = DocumentSizeClassifier.Classify(doc.Profile);
        var headingCount = doc.Headings.Count;
        var usable       = SectionChecker.HasUsableSections(headingCount, doc.Profile);

        var route = usable || (sizeClass == DocumentSizeClass.Small && headingCount >= MinHeadingsWhenSmall)
            ? ChunkingRoute.DeclaredBoundary
            : ChunkingRoute.Recursive;

        return new SectionGateVerdict(sizeClass, headingCount, usable, route);
    }
}

// One document's routing context: the document, the identity resolved for it in step 1, and
// the gate's verdict - together, everything both the chunk pass and the report row need.
//
// Built for every document before any of them is cut, so a run that dies mid-chunking can
// still report what every document's route was.
public sealed record RoutedDocument(
    PdfExtractionDocument Doc,
    DocumentFamily?       Family,
    string?               VectorSource,
    SectionGateVerdict    Gate);
