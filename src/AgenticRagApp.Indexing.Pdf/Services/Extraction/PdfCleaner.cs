using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Cleans extracted PDF page records for RAG indexing.
//
// End goal - every transform must either:
//   - repair extraction damage (mojibake, ligatures, broken hyphenation),
//   - strip characters that add embedding/search noise without carrying meaning
//     (control chars, zero-width chars, whitespace debris), or
//   - normalize structure DI encodes as raw HTML back into plain markdown
//     (table tags - see ConvertTablesToMarkdown) so no downstream stage has to work
//     around HTML sitting inside an otherwise-markdown text field.
// Nothing here rewrites or paraphrases actual content - chunking and retrieval need
// the source text, just undamaged. One bad page becomes an error-severity PipelineIssue; it never
// aborts the whole run.
//
// Explicitly out of scope:
//   - Duplicate (BlobName, PageNumber) pages - that's an extractor invariant
//     violation, asserted once in PdfPipelineValidator, not here.
//   - Header/footer/boilerplate stripping - Contoso's PDF conventions aren't
//     confirmed yet, and a wrong regex here silently deletes real content, which is
//     worse for RAG than leaving a repeated footer in. Add once real sample PDFs
//     confirm the patterns. Document Intelligence can already exclude
//     pageHeader/pageFooter roles at extraction time - prefer solving it there.
public class PdfCleaner : IPdfCleaner
{
    private readonly ILogger<PdfCleaner> _logger;

    public PdfCleaner(ILogger<PdfCleaner>? logger = null) => _logger = logger ?? NullLogger<PdfCleaner>.Instance;

    // Windows-1252 with exception fallbacks on both sides: any character that can't
    // round-trip losslessly throws instead of silently becoming '?' (encode) or a
    // replacement char (decode). RepairMojibake treats either as "not mojibake" and
    // keeps the original text - for RAG, an unrepaired page beats a corrupted one.
    // Requires Encoding.RegisterProvider(CodePagesEncodingProvider.Instance) at
    // startup (see program.cs) and the System.Text.Encoding.CodePages package.
    private static readonly Encoding Win1252Strict = Encoding.GetEncoding(
        1252, new EncoderExceptionFallback(), new DecoderExceptionFallback());

    // Collapse 3+ consecutive newlines down to a single blank line.
    private static readonly Regex ExcessBlankLines = new(@"\n{3,}", RegexOptions.Compiled);

    // Runs of spaces/tabs -> single space. PDF text extraction frequently emits
    // alignment gaps as multiple spaces; they carry layout, not meaning, and waste
    // embedding tokens.
    private static readonly Regex ExcessSpaces = new(@"[ \t]{2,}", RegexOptions.Compiled);

    // Trailing whitespace before a newline - pure noise from line-based extraction.
    private static readonly Regex TrailingLineSpace = new(@"[ \t]+\n", RegexOptions.Compiled);

    // A word split across a line break by end-of-line hyphenation:
    // "informa-\ntie" -> "informatie". Requires lowercase letters on BOTH sides so
    // legitimate hyphenated compounds at a line break ("ADL-ondersteuning") and
    // list-dash lines are left alone. Matters for RAG: a split word matches neither
    // the query embedding nor keyword search.
    private static readonly Regex LineBreakHyphenation =
        new(@"(?<=\p{Ll})-\n(?=\p{Ll})", RegexOptions.Compiled);

    // Control chars except \n and \t. PDF extractors leak these (form feeds, vertical
    // tabs, stray NULs) and they poison both embeddings and JSON payloads downstream.
    private static readonly Regex ControlChars =
        new(@"[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]", RegexOptions.Compiled);

    // Invisible characters that break tokenization and exact-match retrieval while
    // rendering as nothing: zero-width space/joiner/non-joiner, BOM, soft hyphen.
    private static readonly Regex InvisibleChars =
        new(@"[\u200B\u200C\u200D\uFEFF\u00AD]", RegexOptions.Compiled);

    // Document Intelligence's markdown OutputContentFormat follows CommonMark, which
    // backslash-escapes ASCII punctuation wherever it could otherwise be read as markdown
    // syntax (e.g. a literal "1-2" mid-sentence escaped to "1\-2" so "-" isn't parsed as a
    // list bullet). That escaping is a markdown-rendering concern, not real content - left
    // in place it's a literal "\-" in the indexed/embedded text that matches neither a
    // plain-text query for "-" nor "\-". Unescapes the exact CommonMark backslash-escape
    // punctuation set (https://spec.commonmark.org/0.31.2/#backslash-escapes), not a guess
    // at which characters DI happens to escape.
    private static readonly Regex MarkdownEscapedPunctuation =
        new(@"\\([!""#$%&'()*+,\-./:;<=>?@\[\\\]^_`{|}~])", RegexOptions.Compiled);

    // Typographic ligatures PDFs embed as single glyphs (e.g. U+FB01 "fi") that won't
    // match a plain-text query in keyword/hybrid search - expand to plain letters.
    // internal: PdfCleanerTests asserts table completeness directly against this.
    internal static readonly (string Ligature, string Expansion)[] LigatureExpansions =
    [
        ("\uFB01", "fi"), ("\uFB02", "fl"), ("\uFB00", "ff"), ("\uFB03", "ffi"), ("\uFB04", "ffl"),
    ];

    // \u2500\u2500 Table HTML -> markdown \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
    // DI renders tables as real HTML with rowspan/colspan (see PdfDocumentIntelligenceAnalyzer's
    // OutputContentFormat comment) - the regexes/grid logic below turn that into a plain
    // GFM pipe table, in place within the page's markdown text. Merged cells are expanded
    // into a full grid so every row has the same column count - a ragged pipe table (rows
    // with different cell counts) renders as broken columns in markdown, which is exactly
    // the "bad table shape" this exists to prevent.
    private static readonly Regex TableRegex = new(
        @"<table\b[^>]*>(.*?)</table\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RowRegex = new(
        @"<tr\b[^>]*>(.*?)</tr\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CellRegex = new(
        @"<(th|td)\b([^>]*)>(.*?)</\1\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CaptionRegex = new(
        @"<caption\b[^>]*>(.*?)</caption\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex InnerTagRegex = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRunRegex = new(@"\s+", RegexOptions.Compiled);

    private static readonly Regex ColSpanRegex = new(
        @"colspan\s*=\s*[""']?(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RowSpanRegex = new(
        @"rowspan\s*=\s*[""']?(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Figure HTML -> caption text, or stripped entirely.
    // DI marks every detected figure as a <figure> placeholder in Content, but never embeds
    // the actual image there (a figure's pixels only exist behind a separate fetch-by-id
    // endpoint - see PdfDocumentIntelligenceAnalyzer.GetFigures) - so the tag itself carries zero
    // retrieval-useful information. If DI attached a caption/alt text, that's real content
    // (a label for what the figure is about) worth keeping; a bare placeholder with neither
    // wastes chunk/embedding budget on nothing and is worse than no tag at all.
    private static readonly Regex FigureRegex = new(
        @"<figure\b[^>]*>(.*?)</figure\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FigCaptionRegex = new(
        @"<figcaption\b[^>]*>(.*?)</figcaption\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AltTextRegex = new(
        @"alt\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public PdfCleanResult CleanPdf(IReadOnlyList<PdfPageRecord> pages)
    {
        var result = new PdfCleanResult();

        foreach (var page in pages)
            CleanSinglePage(page, result);

        return result;
    }

    private void CleanSinglePage(PdfPageRecord page, PdfCleanResult result)
    {
        try
        {
            var (content, mojibakeFixed, counts) = CleanPageContent(page.PageContent ?? "");
            result.AddCleaningCounts(counts);
            var (tableConverted, tableFallbacks) = ConvertTablesToMarkdown(content);
            content = tableConverted;
            if (tableFallbacks > 0)
            {
                for (var i = 0; i < tableFallbacks; i++)
                    result.CountTableConversionFallback();
                result.AddIssue(PipelineIssue.Warning(
                    PipelineStage.TableStructure,
                    page.BlobName,
                    $"Page {page.PageNumber}: {tableFallbacks} table(s) had an unparseable shape (no rows/empty grid/zero columns) - fell back to plain text instead of a markdown table."));
            }
            content = ConvertFigures(content);

            // Stripping an empty <figure> placeholder can leave a blank line behind where
            // the tag used to sit - re-collapse before this reaches the index, same pass
            // CleanPageContent already applies to everything else.
            content = ExcessBlankLines.Replace(content, "\n\n").Trim();

            if (mojibakeFixed)
            {
                result.CountMojibakeRepaired();
                result.AddIssue(PipelineIssue.Warning(
                    PipelineStage.TextQuality,
                    page.BlobName,
                    $"Page {page.PageNumber}: repaired mojibake in source text (round-trip re-decode)."));
            }

            if (string.IsNullOrWhiteSpace(content))
                result.AddIssue(PipelineIssue.Warning(
                    PipelineStage.Clean,
                    page.BlobName,
                    $"PageContent is empty after cleanup (page {page.PageNumber}) - likely a blank source page."));

            result.AddRecord(ToCleanedRecord(page, content));
        }
        catch (Exception ex)
        {
            // PipelineIssue.Message (shared with CSV's cleaner) only ever carries
            // ex.Message - full exception type/stack trace previously had nowhere to go
            // and was discarded. Logged here instead so a cleaning bug is actually
            // diagnosable from Application Insights, without changing PipelineIssue's
            // shape or the JSON reports built from it.
            _logger.LogError(ex, "Cleaning failed for '{Blob}' page {Page}.", page.BlobName, page.PageNumber);
            result.AddIssue(PipelineIssue.Error(PipelineStage.Clean, page.BlobName, ex.Message));
        }
    }

    private static CleanedPdfPageRecord ToCleanedRecord(PdfPageRecord page, string content) => new()
    {
        BlobName    = page.BlobName,
        PageNumber  = page.PageNumber,
        PageContent = content,
        Title       = TrimOrEmpty(page.Title),
    };

    private static string TrimOrEmpty(string? value) => value?.Trim() ?? "";

    // Cleanup order, deliberate:
    //   1. Line endings first - every later regex only has to reason about \n.
    //   2. Mojibake repair before anything else that inspects characters -
    //      downstream steps should see the *real* text.
    //   3. Character-level cleanup: control/invisible chars, ligatures, NBSP.
    //   4. Markdown punctuation-escape removal before hyphenation repair - an escaped
    //      "\-" at a line break must become a plain "-" first, or LineBreakHyphenation's
    //      "-\n" pattern never matches it.
    //   5. NFC normalization - accented letters are always one codepoint; composed
    //      vs. decomposed forms embed and keyword-match differently, which is silent
    //      retrieval noise.
    //   6. Hyphenation repair before whitespace collapse - it consumes a \n.
    //   7. Whitespace last, over the fully repaired text.
    // Table HTML -> markdown (ConvertTablesToMarkdown) runs separately, after this whole
    // pipeline (see CleanSinglePage) - it changes structure rather than repairing/stripping
    // characters, and it builds its own clean spacing, so it doesn't need to precede or be
    // followed by any of the character/whitespace passes below.
    private static (string Content, bool MojibakeFixed, PdfCleaningCounts Counts) CleanPageContent(string raw)
    {
        var text = raw.Replace("\r\n", "\n").Replace("\r", "\n");

        (text, var mojibakeFixed) = RepairMojibake(text);

        var controlCharCount = ControlChars.Matches(text).Count;
        text = ControlChars.Replace(text, "");

        var invisibleCharCount = InvisibleChars.Matches(text).Count;
        text = InvisibleChars.Replace(text, "");

        var ligatureCount = 0;
        foreach (var (ligature, expansion) in LigatureExpansions)
        {
            ligatureCount += text.Count(c => c == ligature[0]);
            text = text.Replace(ligature, expansion);
        }
        text = text.Replace('\u00A0', ' '); // NBSP -> plain space

        text = MarkdownEscapedPunctuation.Replace(text, "$1");

        text = text.Normalize(NormalizationForm.FormC);

        var hyphenJoinCount = LineBreakHyphenation.Matches(text).Count;
        text = LineBreakHyphenation.Replace(text, "");

        text = TrailingLineSpace.Replace(text, "\n");
        text = ExcessSpaces.Replace(text, " ");
        text = ExcessBlankLines.Replace(text, "\n\n");

        var counts = new PdfCleaningCounts(controlCharCount, invisibleCharCount, ligatureCount, hyphenJoinCount);
        return (text.Trim(), mojibakeFixed, counts);
    }

    // Repairs the entire Windows-1252/UTF-8 mis-decode class in one round-trip
    // instead of enumerating symptoms pattern by pattern. Signature-gated so clean
    // text (the overwhelmingly common case with Document Intelligence) skips the
    // re-decode entirely.
    private static (string Text, bool Fixed) RepairMojibake(string text)
    {
        // U+00C3 and U+00E2 are the fingerprint of UTF-8 bytes read as Windows-1252.
        if (!text.Contains('\u00C3') && !text.Contains('\u00E2'))
            return (text, false);

        try
        {
            var repaired = Encoding.UTF8.GetString(Win1252Strict.GetBytes(text));

            // U+FFFD means the round-trip failed - the text was legitimate (e.g. a
            // genuine U+00E2 in a loanword), not mojibake.
            return repaired.Contains('\uFFFD') ? (text, false) : (repaired, true);
        }
        catch (EncoderFallbackException)
        {
            // text has characters outside Windows-1252 (arrows, checkboxes, non-Latin
            // scripts, etc.) - genuine content, not mojibake. Leave it untouched.
            return (text, false);
        }
    }

    // Cheap substring check before the regex engine ever runs - the overwhelming majority
    // of pages have no table at all, and shouldn't pay for a Singleline scan over the whole
    // page just to find nothing. fallbackCount is a local (not a ref parameter) precisely
    // so it can be captured and mutated from the MatchEvaluator lambda below - a ref
    // parameter can't be captured by a lambda (CS1628).
    private static (string Content, int FallbackCount) ConvertTablesToMarkdown(string content)
    {
        if (!content.Contains("<table", StringComparison.OrdinalIgnoreCase)) return (content, 0);

        var fallbackCount = 0;
        var result = TableRegex.Replace(content, m => ConvertTable(m.Groups[1].Value, ref fallbackCount));
        return (result, fallbackCount);
    }

    // On a shape ConvertTable can't parse (no rows, empty grid, zero columns), the whole
    // <table>...</table> block used to be deleted outright by returning "" here - the
    // cell text (real content) vanished from the indexed page with no signal at all.
    // Falling back to CleanCellContent's tag-stripped, decoded, single-line text keeps
    // that content (as plain text, not a pipe table) instead of discarding it, and
    // fallbackCount makes the substitution visible instead of silent (finding #16).
    private static string ConvertTable(string tableInner, ref int fallbackCount)
    {
        var rowMatches = RowRegex.Matches(tableInner);
        if (rowMatches.Count == 0) { fallbackCount++; return CleanCellContent(tableInner); }

        var grid = BuildGrid(rowMatches);
        if (grid.Count == 0) { fallbackCount++; return CleanCellContent(tableInner); }

        var columnCount = grid.Max(r => r.Count);
        if (columnCount == 0) { fallbackCount++; return CleanCellContent(tableInner); }

        foreach (var row in grid)
            while (row.Count < columnCount)
                row.Add("");

        var sb = new StringBuilder();

        var captionMatch = CaptionRegex.Match(tableInner);
        if (captionMatch.Success)
        {
            var caption = CleanCellContent(captionMatch.Groups[1].Value);
            if (!string.IsNullOrWhiteSpace(caption))
            {
                sb.Append(caption);
                sb.Append('\n');
                sb.Append('\n');
            }
        }

        AppendRow(sb, grid[0]);
        sb.Append('\n');
        sb.Append('|');
        for (var i = 0; i < columnCount; i++)
            sb.Append(" --- |");
        sb.Append('\n');

        for (var r = 1; r < grid.Count; r++)
        {
            AppendRow(sb, grid[r]);
            if (r < grid.Count - 1) sb.Append('\n');
        }

        return sb.ToString();
    }

    private static void AppendRow(StringBuilder sb, List<string> row)
    {
        sb.Append('|');
        foreach (var cell in row)
        {
            sb.Append(' ');
            sb.Append(cell);
            sb.Append(" |");
        }
    }

    // Resolves rowspan/colspan into a dense grid - one entry per (row, column), duplicating
    // a merged cell's text into every cell it visually spans. "pending" tracks a rowspan
    // cell's remaining life at each column so the following row(s) place it before pulling
    // their own next <td>/<th>, exactly like a browser lays out an HTML table.
    private static List<List<string>> BuildGrid(MatchCollection rowMatches)
    {
        var grid = new List<List<string>>();
        var pending = new Dictionary<int, (int Remaining, string Text)>();

        foreach (Match rowMatch in rowMatches)
        {
            var cellMatches = CellRegex.Matches(rowMatch.Groups[1].Value);
            var row = new List<string>();
            var col = 0;
            var ci = 0;

            while (ci < cellMatches.Count || pending.ContainsKey(col))
            {
                if (pending.TryGetValue(col, out var carry))
                {
                    row.Add(carry.Text);
                    if (carry.Remaining - 1 > 0)
                        pending[col] = (carry.Remaining - 1, carry.Text);
                    else
                        pending.Remove(col);
                    col++;
                    continue;
                }

                if (ci >= cellMatches.Count) break;

                var cellMatch = cellMatches[ci++];
                var attrs     = cellMatch.Groups[2].Value;
                var colSpan   = ParseSpan(ColSpanRegex, attrs);
                var rowSpan   = ParseSpan(RowSpanRegex, attrs);
                var text      = CleanCellContent(cellMatch.Groups[3].Value);

                for (var i = 0; i < colSpan; i++)
                {
                    row.Add(text);
                    if (rowSpan > 1)
                        pending[col + i] = (rowSpan - 1, text);
                }
                col += colSpan;
            }

            grid.Add(row);
        }

        return grid;
    }

    private static int ParseSpan(Regex regex, string attrs)
    {
        var m = regex.Match(attrs);
        return m.Success && int.TryParse(m.Groups[1].Value, out var v) && v > 0 ? v : 1;
    }

    // Strips any nested markup (<br>, <sup>, DI's own <br/> line breaks, etc.), decodes HTML
    // entities, and collapses the cell down to one line - a pipe-table cell can't contain a
    // literal newline, and a literal '|' would be misread as a column boundary.
    private static string CleanCellContent(string raw)
    {
        var text = InnerTagRegex.Replace(raw, " ");
        text = WebUtility.HtmlDecode(text);
        text = WhitespaceRunRegex.Replace(text, " ").Trim();
        return text.Replace("|", "\\|");
    }

    // Cheap substring check before the regex engine ever runs - same reasoning as
    // ConvertTablesToMarkdown: most pages have no figure at all.
    private static string ConvertFigures(string content)
    {
        if (!content.Contains("<figure", StringComparison.OrdinalIgnoreCase)) return content;

        return FigureRegex.Replace(content, m => ConvertFigure(m.Groups[1].Value));
    }

    // Prefers a <figcaption>, then an <img alt="...">, over the bare tag - either is real
    // content describing what the figure is about. Neither present means DI gave us nothing
    // usable, so the whole placeholder is dropped rather than left as dead HTML.
    private static string ConvertFigure(string figureInner)
    {
        var captionMatch = FigCaptionRegex.Match(figureInner);
        if (captionMatch.Success)
        {
            var caption = CleanCellContent(captionMatch.Groups[1].Value);
            if (!string.IsNullOrWhiteSpace(caption)) return caption;
        }

        var altMatch = AltTextRegex.Match(figureInner);
        if (altMatch.Success)
        {
            var alt = CleanCellContent(altMatch.Groups[1].Value);
            if (!string.IsNullOrWhiteSpace(alt)) return alt;
        }

        return "";
    }
}
