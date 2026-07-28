using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgenticRagApp.Indexing.Csv.Models;
using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Indexing.Csv.Services;

// Cleans joined page records: strips boilerplate/markup from content,
// parses raw string fields into typed values, and de-duplicates pages.
// One bad page becomes a CleaningError; it never aborts the whole run.
public class DataCleaner : IDataCleaner
{
    // Standalone "cordaan"/"CORDAAN" logo lines only — lowercase and all-caps are
    // both confirmed logo text; mixed-case "Cordaan" is left untouched since that's
    // where real prose (org charts, sentences) shows up. Leading/trailing
    // spaces/tabs around the logo line are tolerated.
    private static readonly Regex CordaanBoilerplate =
        new(@"^[ \t]*(cordaan|CORDAAN)[ \t]*$\n?", RegexOptions.Multiline | RegexOptions.Compiled);

    // Markdown image placeholders, e.g. ![alt](path) — carry no text value.
    private static readonly Regex ImagePlaceholder =
        new(@"!\[[^\]]*\]\([^\)]*\)", RegexOptions.Compiled);

    // Literal HTML/XML tags in the *raw* (pre-decode) text (e.g. <br/>,
    // <concept factuur="" ...>). Applied before HtmlDecode so escaped text like
    // "&lt;naam&gt;" survives as visible "<naam>" content instead of being
    // stripped as markup. Requires the first character after '<' (or '</') to be
    // a letter so stray comparison operators like "a < b" aren't touched.
    private static readonly Regex HtmlTag =
        new(@"<\/?[a-zA-Z][^<>]*>", RegexOptions.Compiled);

    // Collapse 3+ consecutive newlines down to a single blank line.
    private static readonly Regex ExcessBlankLines =
        new(@"\n{3,}", RegexOptions.Compiled);

    // Windows-1252/UTF-8 mis-decodes seen in Zenya's source exports. Dutch text is full
    // of accented characters (é, ë, ï, ü) and curly quotes, so this class of corruption
    // is common here. Checked in order; longer patterns first so "â€œ" isn't left
    // half-replaced as a prefix match.
    private static readonly (string Pattern, string Fix)[] KnownMojibakePatterns =
    [
        ("â€™", "'"), ("â€œ", "\""), ("â€", "\""), ("â€“", "–"), ("â€”", "—"),
        ("Ã«", "ë"), ("Ã©", "é"), ("Ã¯", "ï"), ("Ã¼", "ü"),
    ];

    // Entry point. Walks all pages, skipping duplicates (same DocumentId +
    // PageIndex) and converting each remaining page to a CleanedPageRecord.
    // Parse failures are collected as errors; empty content only warns.
    public CleanResult Clean(IReadOnlyList<JoinedPageRecord> pages)
    {
        var result   = new CleanResult();
        var seenKeys = new HashSet<(string DocId, int Page)>();

        foreach (var page in pages)
        {
            if (!seenKeys.Add((page.DocumentId, page.PageIndex)))
            {
                ReportDuplicatePage(page, result);
                continue;
            }

            CleanSinglePage(page, result);
        }

        return result;
    }

    // Records a skipped duplicate page (first occurrence wins).
    private static void ReportDuplicatePage(JoinedPageRecord page, CleanResult result)
    {
        result.CountDuplicateSkipped();
        result.AddWarning(new CleaningWarning(
            DocumentId: page.DocumentId,
            Message:    $"Duplicate page {page.PageIndex} in source — kept the first occurrence."));
    }

    // Cleans one page and adds it to the result.
    // Any parse failure (dates, flags) lands in result.Errors for that page only.
    private static void CleanSinglePage(JoinedPageRecord page, CleanResult result)
    {
        try
        {
            var (content, mojibakeFixed) = CleanPageContent(page.PageContent ?? "");

            if (mojibakeFixed)
            {
                result.CountMojibakeRepaired();
                result.AddWarning(new CleaningWarning(
                    DocumentId: page.DocumentId,
                    Message:    $"Page {page.PageIndex}: repaired mojibake in source text (e.g. 'â€™' -> \"'\")."));
            }

            if (string.IsNullOrWhiteSpace(content))
                result.AddWarning(new CleaningWarning(
                    DocumentId: page.DocumentId,
                    Message:    $"PageContent is empty after cleanup (page {page.PageIndex}) — likely a blank source page."));

            result.AddRecord(ToCleanedRecord(page, content));
        }
        catch (Exception ex)
        {
            result.AddError(new CleaningError(DocumentId: page.DocumentId, Message: ex.Message));
        }
    }

    // Maps a joined record to its cleaned, typed equivalent:
    // trims string fields (null-safe), parses dates and attention flags.
    private static CleanedPageRecord ToCleanedRecord(JoinedPageRecord page, string content) => new()
    {
        DocumentId       = page.DocumentId,
        Title            = TrimOrEmpty(page.Title),
        QuickCode        = TrimOrEmpty(page.QuickCode),
        FolderPath       = TrimOrEmpty(page.FolderPath),
        LastModified     = ParseDateTime(page.LastModifiedRaw, "yyyyMMddHHmmss", page.DocumentId, "LastModifiedRaw"),
        PageIndex        = page.PageIndex,
        PageContent      = content,
        Language         = TrimOrEmpty(page.Language),
        RelativePath     = TrimOrEmpty(page.RelativePath),
        DocumentTypeName = TrimOrEmpty(page.DocumentTypeName),
        Summary          = TrimOrEmpty(page.Summary),
        Version          = TrimOrEmpty(page.Version),
        CheckDate        = ParseOptionalDateTime(page.CheckDateRaw, "yyyyMMdd", page.DocumentId, "CheckDateRaw"),
        AttentionFlags   = ParseAttentionFlags(page.AttentionFlagsRaw, page.DocumentId),
    };

    // Null-safe trim: a missing source field becomes an empty string
    // instead of throwing NullReferenceException.
    private static string TrimOrEmpty(string? value) => value?.Trim() ?? "";

    // Normalizes page text: normalize line endings, strip literal HTML tags
    // *before* decoding (so escaped markup survives as text), then HTML-decode,
    // repair known mojibake, strip logo lines and image placeholders, collapse
    // excess blank lines, trim.
    private static (string Content, bool MojibakeFixed) CleanPageContent(string raw)
    {
        var text = raw.Replace("\r\n", "\n");
        text = HtmlTag.Replace(text, "");
        text = WebUtility.HtmlDecode(text);
        // Decoding can emit \r from entities (&#13;); normalize again so the
        // multiline $ anchors below aren't defeated by stray carriage returns.
        text = text.Replace("\r\n", "\n").Replace("\r", "\n");

        var mojibakeFixed = false;
        foreach (var (pattern, fix) in KnownMojibakePatterns)
        {
            if (!text.Contains(pattern)) continue;
            text          = text.Replace(pattern, fix);
            mojibakeFixed = true;
        }

        text = CordaanBoilerplate.Replace(text, "");
        text = ImagePlaceholder.Replace(text, "");
        text = ExcessBlankLines.Replace(text, "\n\n");
        return (text.Trim(), mojibakeFixed);
    }

    // Parses a required date in the given exact format; throws FormatException
    // with document context if it doesn't match (a null field also fails here).
    private static DateTime ParseDateTime(string? raw, string format, string documentId, string field)
    {
        if (!DateTime.TryParseExact(raw, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            throw new FormatException($"{field} '{raw}' on document {documentId} is not a valid '{format}' date.");
        return value;
    }

    // Same as ParseDateTime, but empty/whitespace/null input means "no date" (null).
    private static DateTime? ParseOptionalDateTime(string? raw, string format, string documentId, string field)
        => string.IsNullOrWhiteSpace(raw) ? null : ParseDateTime(raw, format, documentId, field);

    // Parses AttentionFlagsRaw as a JSON string array; empty input → empty list,
    // invalid JSON → FormatException. Null/whitespace-only entries inside the
    // array are dropped, and remaining entries are trimmed.
    private static List<string> ParseAttentionFlags(string? raw, string documentId)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        try
        {
            var flags = JsonSerializer.Deserialize<List<string?>>(raw) ?? [];
            return flags
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(f => f!.Trim())
                .ToList();
        }
        catch (JsonException ex)
        {
            throw new FormatException($"AttentionFlagsRaw '{raw}' on document {documentId} is invalid JSON: {ex.Message}");
        }
    }
}
