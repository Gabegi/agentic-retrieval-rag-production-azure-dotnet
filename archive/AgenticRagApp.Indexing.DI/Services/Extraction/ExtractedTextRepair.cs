using System.Text;
using System.Text.RegularExpressions;

namespace AgenticRagApp.Indexing.DI.Services;

// The character repairs every piece of extracted text needs, whatever path it took here.
//
// PdfCleaner.CleanPageContent applies this class of repair to page BODIES, and did so alone -
// which is exactly how the 260818 index ended up with 508 decomposed U+0308 combining marks and
// a title field reading "Hygienecode" while its own breadcrumbs read "Hygiënecode": titles
// (native PDF metadata or the blob name) and DI headings never pass through the cleaner, so the
// same letter reached the index as two different byte sequences depending on which field it sat
// in. Exact-term matching then sees two different words.
//
// One function, called from every entry path: page bodies (PdfCleaner), titles
// (GetTitleHelper), headings (HeadingTextNormalizer.Flatten, the single funnel heading_text and
// heading_path flow through), and the needle HeadingLocator matches against cleaned content -
// which MUST apply the same transforms as the content it searches, or a heading whose raw form
// differs from its cleaned form silently fails to locate.
//
// FoldSymbols is separate from Repair because PdfCleaner already runs its own counted versions
// of the removal passes (it reports per-run cleaning counts) and only needs the fold added on
// top; everything else takes Repair whole.
internal static partial class ExtractedTextRepair
{
    // Same ranges as PdfCleaner's counted ControlChars pass - kept identical on purpose.
    [GeneratedRegex(@"[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]")]
    private static partial Regex ControlChars();

    // Zero-widths, BOM, soft hyphen - characters that render as nothing and break matching.
    // Same set as PdfCleaner's counted InvisibleChars pass.
    [GeneratedRegex(@"[\u200B\u200C\u200D\uFEFF\u00AD]")]
    private static partial Regex InvisibleChars();

    // Single-glyph unit symbols folded to their searchable spelling. U+2103 is the measured
    // case: 788 occurrences of "℃" in the 260818 index and not one "°C" - against a
    // corpus whose hygiene queries ask about temperature thresholds. NFC preserves these
    // glyphs (only NFKC would decompose them, and NFKC is too aggressive to run on legal
    // text), so the fold has to be explicit.
    private static readonly (string From, string To)[] SymbolFolds =
    [
        ("℃", "°C"),   // ℃ degree celsius, one glyph
        ("℉", "°F"),   // ℉ degree fahrenheit
        ("№", "nr."),  // № numero sign
    ];

    public static string FoldSymbols(string text)
    {
        foreach (var (from, to) in SymbolFolds)
            text = text.Replace(from, to);

        return text;
    }

    // The full pass, for text that reached us WITHOUT going through PdfCleaner.
    public static string Repair(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        text = ControlChars().Replace(text, "");
        text = InvisibleChars().Replace(text, "");

        foreach (var (ligature, expansion) in PdfCleaner.LigatureExpansions)
            text = text.Replace(ligature, expansion);

        text = text.Replace('\u00A0', ' ');  // NBSP -> plain space
        text = FoldSymbols(text);

        // Last, so the fold and expansions above are themselves normalized. FormC composes
        // "e" + U+0308 into "ë" - one letter, one byte sequence, everywhere.
        return text.Normalize(NormalizationForm.FormC);
    }
}
