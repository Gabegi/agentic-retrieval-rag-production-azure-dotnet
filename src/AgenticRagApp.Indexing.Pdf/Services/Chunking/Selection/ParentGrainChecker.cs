namespace AgenticRagApp.Indexing.Pdf.Services;

// Step 2 of DetermineStrategy: is the retrieval parent the whole document, or a section?
//
// Answered from the size class alone - the reasoned-but-unvalidated 4,000-token line
// (chunking-signals-map.md): Small means returning the whole document costs about what one
// generous chunk costs, so a parent/child hierarchy buys nothing there. A placeholder
// answer, not a verdict: nothing dispatches on it yet - it flows through the decision
// record and the run report so eval runs can teach us more before any code trusts it.
//
// DocumentProfile.DocumentIsSafeReturnUnit (null until Phase D measures the return bound)
// is deliberately NOT consulted - a precedence branch for a measurement that does not
// exist would be dead code. When Phase D lands, this is where the measured value takes
// over from the size class.
public static class ParentGrainChecker
{
    public static ParentGrain Determine(DocumentSizeClass sizeClass) =>
        sizeClass == DocumentSizeClass.Small
            ? ParentGrain.WholeDocument
            : ParentGrain.ParentChild;
}
