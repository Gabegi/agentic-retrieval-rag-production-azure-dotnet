namespace AgenticRagApp.Indexing.Pdf.Utils;

// The ONE shape a heading takes once it is stored on a chunk or rendered into a heading path.
//
// It exists because there were two. A heading merged at extraction carries both lines in
// Content ("Artikel 9\nBegrippen"), and the code that consumed it disagreed about what to do
// with the newline: HeadingLocator stored heading.Content.Trim() whole for an ordinary section -
// newline included - while its paired-merge branch stored a space-joined pair, and
// HeadingChainBuilder took the first line only and dropped the rest. Same phenomenon, three
// answers, all three flowing into heading_text, heading_path, the embedded prefix and therefore
// into ContentHash.
//
// A newline is the wrong answer everywhere it landed. heading_path renders as
// "Hoofdstuk 3 > Artikel 9\nBegrippen" in a citation, the prefix puts a line break in the middle
// of the embedded title line, and Search stores a field whose value spans lines. Dropping the
// second line is also wrong: "Begrippen" is what the article is ABOUT, and it is the half a
// query matches on.
//
// So: keep every line, join with a single space. Note this is deliberately NOT what
// HeadingLocator matches on - locating a merged heading in the cleaned text still uses the FIRST
// line, because that is the only part guaranteed contiguous there (the merged Offset covers the
// first paragraph only). Matching and storing are different jobs and this is only the second.
public static class HeadingTextNormalizer
{
    private static readonly char[] LineBreaks = ['\n', '\r'];

    // Character repair rides on the same funnel (Services.ExtractedTextRepair): heading text
    // comes off DI's RAW content and never passes through PdfCleaner, which is how the 260818
    // index carried 508 decomposed U+0308 marks in heading fields while every page body was
    // NFC-clean. Flatten is the one place all heading_text and heading_path values flow
    // through, so repairing here covers both - and the embedded prefix built from them.
    public static string? Flatten(string? content) =>
        string.IsNullOrWhiteSpace(content)
            ? null
            : Services.ExtractedTextRepair.Repair(
                  string.Join(' ', content.Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries)
                                          .Select(line => line.Trim())
                                          .Where(line => line.Length > 0)));
}
