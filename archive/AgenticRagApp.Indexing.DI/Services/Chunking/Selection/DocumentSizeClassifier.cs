using AgenticRagApp.Indexing.DI.Models;

namespace AgenticRagApp.Indexing.DI.Services;

// Size class, from the routing measurements (docs/2608/260811/chunking-signals-map.md).
// Picture is decided on density/extraction loss, the other three on estimated tokens.
//
// It lives beside the classifier that produces it, rather than with the routing decision record
// it used to share a file with: that record and the four-way strategy vocabulary around it are
// gone, and this is the only surviving member of that group. Nothing routes on the class any
// more - the gate reads the token count directly - so what remains is a REPORTED signal, read by
// the metadata stamp and the run report's per-document row.
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
