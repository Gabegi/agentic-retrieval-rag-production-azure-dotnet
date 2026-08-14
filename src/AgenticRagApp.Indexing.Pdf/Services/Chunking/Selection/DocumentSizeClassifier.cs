using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Step 1 of DetermineStrategy: which size class is this document?
//
// Picture is read off the profile's own HasExtractableContent (computed at extraction from
// charsPerPage < 1,000 OR bytesPerChar >= 100 - see DocumentProfileHelper) rather than
// re-deriving those thresholds here, so the rule exists in exactly one place.
public static class DocumentSizeClassifier
{
    // 50,000 sits in a real gap in the corpus's token distribution (~25.1k -> ~90.9k,
    // nothing between) - a measured fact, not a tuning knob.
    public const int LargeTokenThreshold = 50_000;

    // The "can the whole document BE the retrieval unit" line. Reasoned, never measured
    // (chunking-signals-map.md); Phase D's return-bound measurement should confirm or move it.
    public const int MediumTokenThreshold = 4_000;

    public static DocumentSizeClass Classify(DocumentProfile? profile)
    {
        // No profile means extraction did not measure this document. Medium is the safe
        // default: it routes to the parent/child cascade and claims nothing about fitting
        // in one returned unit.
        if (profile is null) return DocumentSizeClass.Medium;

        if (!profile.HasExtractableContent) return DocumentSizeClass.Picture;

        return profile.EstimatedTokens switch
        {
            >= LargeTokenThreshold  => DocumentSizeClass.Large,
            >= MediumTokenThreshold => DocumentSizeClass.Medium,
            _                       => DocumentSizeClass.Small,
        };
    }
}
