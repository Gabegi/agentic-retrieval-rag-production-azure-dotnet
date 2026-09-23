using System.Text.RegularExpressions;

namespace AgenticRagApp.Indexing.CU.Services;

// Page furniture in Content Understanding's markdown (2026-09-23, D224 A1).
//
// CU writes a page's header, number and page turn into the markdown as HTML comments -
// `<!-- PageHeader: ![contoso](figures/1.1 "The logo of contoso.") -->`, `<!-- PageNumber: 2026 -->`,
// `<!-- PageBreak -->` - and they ride inside chunk Content like any other text. Measured on run
// 260922/2 (D223 F1): 2,015 chunks were nothing but these comments plus a scrap under the
// residue floor, and they were indexed because the comment LABELS alone clear it
// ("PageNumber2026" is 14 alphanumerics against a floor of 4).
//
// ONE definition of what furniture is, read by every rule that needs it - today the residue rule
// (ChunkingService.IsResidue), later the embedding-text strip (D224 B2). A second copy of this
// list elsewhere is the drift this class exists to prevent.
//
// Two things are deliberately NOT furniture:
//
// - PageFooter. CU wraps real footnotes in it: 713 chunks on 260922/2 are a PageFooter comment
//   carrying the footnote text of the page (D223 F7). A length cut cannot tell those from a
//   repeated document name ("Ontruimingsplan" 62x, "ANSUL Brandbeveiliging" 65x), so footers
//   wait for a rule that reads the typed Boilerplate role instead.
// - Standalone figure markdown `![...](figures/...)`. Its alt text is CU's generated figure
//   description - real content. Stripping it would have dropped 354 more chunks on 260922/2, and
//   nearly doubled the dropped tokens (104,401 -> 192,602). A page-HEADER figure is still
//   removed, because it sits inside a PageHeader comment and goes with it.
//
// Nothing here rewrites Content. Callers use the stripped string to JUDGE a chunk; the slice
// invariant (Content == doc.Content[Start..Start+Length]) is untouched.
public static partial class PageMarkup
{
    // Lazy and Singleline so a comment that wraps a line is removed whole. An unclosed comment
    // would run to the next "-->" and hide the text between - acceptable because the result is
    // only ever counted, never stored, and CU does not emit unclosed comments.
    [GeneratedRegex(@"<!--\s*(PageHeader|PageNumber|PageBreak)\b.*?-->", RegexOptions.Singleline)]
    private static partial Regex Furniture();

    public static string StripFurniture(string text) => Furniture().Replace(text, "");
}
