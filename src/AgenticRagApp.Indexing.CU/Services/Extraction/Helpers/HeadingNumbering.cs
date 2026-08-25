using System.Text.RegularExpressions;

namespace AgenticRagApp.Indexing.CU.Services;

// The two patterns that decide whether a heading carries a numbering cross-check, kept in one
// place because they must agree with each other on what counts as a numeral - a heading could
// otherwise be "numbered" for the profile's share while not being a bare label for the merge, on
// nothing but a single ambiguous letter.
//
// Both were GeneratedRegex members of GetHeadingsHelper until that class was deleted with the
// Document Intelligence pipeline. Neither is DI-specific: they match heading TEXT, whichever
// service produced it, and their consumers sit on both sides of the extraction/chunking line
// (DocumentProfileHelper reads one, HeadingLocator the other). That split is exactly why they
// live here rather than inside either caller.
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

    // Broader than BareNumberedLabelWithWord above, which only matches a bare label with no
    // title merged in. This matches a heading that carries a numbering cross-check at all, title
    // text or not - both numbering shapes confirmed in the corpus: a word+number label at the
    // very start ("Artikel 9 Vakantie", "Hoofdstuk IV") and a pure dotted-number prefix ("1.1
    // Voedselveiligheid...", "10. Producten bereiden" - the exact regex
    // hygienecode-numbering-findings.md validated reproduces Pass 2's 32% figure).
    // DocumentProfileHelper uses this for B4's per-document numbered-heading share.
    [GeneratedRegex(@"^(?:\p{L}+\s+(?:\d+(?:\.\d+)*|[IVXLCDM]{2,})\b|\d+(?:\.\d+)*\.?)(?=\s|$)")]
    internal static partial Regex NumberedHeadingPrefix();
}
