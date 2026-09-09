using System.Text.RegularExpressions;

namespace AgenticRagApp.Indexing.CU.Services;

// What counts as a numbered heading label. One pattern now (see the note at the bottom for the
// second one and why it went), read by HeadingLocator's bare-label merge.
//
// It was a GeneratedRegex member of GetHeadingsHelper until that class was deleted with the
// Document Intelligence pipeline. It is not DI-specific: it matches heading TEXT, whichever
// service produced it, which is why it lives here rather than inside its caller.
internal static partial class HeadingNumbering
{
    // A BARE numbered label with no title merged into it: "Artikel 9", "Bijlage XII",
    // "Hoofdstuk IV".
    //
    // Roman branch requires 2+ letters so a single ambiguous letter (a stray "I" or "V" that is
    // really a word) cannot make an ordinary heading read as a label - no real case in the
    // corpus is lost to that. Still a pattern match rather than a validity check ("Bijlage
    // CIVIL" would match), which stays acceptable for the same reason: zero such cases in the
    // corpus. Label word captured (group 1) for the vocabulary-discovery signal.
    [GeneratedRegex(@"^(\p{L}+)\s+(?:\d+(?:\.\d+)*|[IVXLCDM]{2,})$")]
    internal static partial Regex BareNumberedLabelWithWord();

    // A second, broader pattern sat here - "does this heading carry a numbering cross-check at
    // all", validated against Pass 2's 32% figure in hygienecode-numbering-findings.md. Its
    // only consumer was DocumentProfile's per-document numbered-heading share (B4), and both
    // the field and the record were deleted 2026-09-08 with the routing decisions they fed. The
    // pattern is recoverable from that doc if a consumer ever appears; keeping an unread regex
    // here would just be the same dead weight one level down.
}
