using System.Text;
using System.Text.RegularExpressions;
using Azure.AI.DocumentIntelligence;
using Microsoft.Extensions.Logging;
using AgenticRagApp.Indexing.DI.Models;
using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Indexing.DI.Services;

// One PdfPageRecord per page, sliced from Content by each page's own Spans (DI's
// structural page model), not by splitting on "<!-- PageBreak -->".
// Per page:
// - Slice by Spans, strip DI's noise comments (PageHeader/Footer/Number/FigureContent),
//   normalize a setext title ("Title" + "===") to ATX ("# Title").
// - Warn if that leaves the page empty; an empty page shouldn't reach the index unnoticed.
// - Warn (never repair) on unbalanced <table> tags: a table split across pages is
//   handled later by the chunk-builder's Sections-based boundaries, so this is a
//   frequency signal, not a fix.
// Both cleanups touch PageContent only, never Content: they change string length,
// which would shift every offset into the offset-addressable source.
internal static partial class GetPagesHelper
{
    public static (IReadOnlyList<PdfPageRecord> Pages,
                    IReadOnlyList<AnalysisWarning> Warnings,
                    IReadOnlyList<AnalysisWarning> Infos) GetPages(
        ILogger logger, AnalyzeResult result, string blobName, string title)
    {
        var pages    = new List<PdfPageRecord>(result.Pages.Count);
        var warnings = new List<AnalysisWarning>();

        var setextNormalized   = 0;
        var noiseStripped      = 0;
        var pagesWithNoise     = 0;
        var truncatedSpans     = 0;

        foreach (var p in result.Pages)
        {
            var content = SliceBySpans(result.Content, p.Spans, ref truncatedSpans);

            // One regex pass each, counting and replacing together: same cost as the
            // plain Replace it stands in for, not two scans per pattern.
            var pageNoise = 0;
            content = NoiseCommentLineRegex().Replace(content, _ => { pageNoise++; return ""; });
            if (pageNoise > 0)
            {
                noiseStripped += pageNoise;
                pagesWithNoise++;
            }

            // Anything still starting with "<!--" is a DI comment type this regex
            // doesn't know about yet - the same silent-leak shape PageBreak was before
            // this method covered it. Surfaced here instead of assuming the known list
            // is exhaustive.
            if (content.Contains("<!--"))
            {
                logger.LogWarning(
                    "'{Blob}' page {Page} still contains an HTML comment after noise stripping; a new DI comment type may need handling.",
                    blobName, p.PageNumber);

                warnings.Add(new AnalysisWarning(
                    "UnrecognizedComment",
                    $"Page {p.PageNumber} still contains an HTML comment after noise stripping; a new DI comment type may need handling.",
                    blobName));
            }

            // TrimEnd: the title group excludes \r and \n but not trailing spaces/tabs,
            // which markdown would otherwise carry into the ATX heading.
            content = SetextTitleRegex().Replace(content, m =>
            {
                setextNormalized++;
                return "# " + m.Groups["title"].Value.TrimEnd();
            });

            content = content.Trim('\r', '\n');

            if (content.Length == 0)
            {
                logger.LogWarning(
                    "'{Blob}' page {Page} has no content (no Spans); an empty page could reach the index unnoticed.",
                    blobName, p.PageNumber);

                warnings.Add(new AnalysisWarning(
                    "EmptyPageContent",
                    $"Page {p.PageNumber} has no content (no Spans); an empty page could reach the index unnoticed.",
                    blobName));
            }

            var open  = TableOpenTagRegex().Count(content);
            var close = TableCloseTagRegex().Count(content);
            if (open != close)
            {
                logger.LogWarning(
                    "'{Blob}' page {Page} has unbalanced <table> tags ({Open} open, {Close} close); likely split across a page boundary.",
                    blobName, p.PageNumber, open, close);

                warnings.Add(new AnalysisWarning(
                    "UnbalancedTableTags",
                    $"Page {p.PageNumber} has {open} <table> open tag(s) but {close} close tag(s); likely split across a page boundary.",
                    blobName));
            }

            pages.Add(new PdfPageRecord
            {
                BlobName    = blobName,
                PageNumber  = p.PageNumber,
                PageContent = content,
                Title       = title,
            });
        }

        if (truncatedSpans > 0)
            warnings.Add(new AnalysisWarning(
                "SpanOutOfRange",
                $"{truncatedSpans} page span(s) fell outside the analyzed content and were clamped; page text may be incomplete.",
                blobName));

        // File-level counts of cosmetic normalization: worth knowing, not a defect,
        // and not worth one entry per page.
        var infos = new List<AnalysisWarning>(2);

        if (setextNormalized > 0)
            infos.Add(new AnalysisWarning(
                "SetextTitleNormalized",
                $"Setext-style title normalized to ATX on {setextNormalized} page(s).",
                blobName));

        if (noiseStripped > 0)
            infos.Add(new AnalysisWarning(
                "NoiseCommentsStripped",
                $"{noiseStripped} DI decoration comment(s) (page header/footer/number/break/figure-content) stripped across {pagesWithNoise} page(s).",
                blobName));

        return (pages, warnings, infos);
    }

    // Concatenates a page's spans in offset order.
    // - Offsets come from the service and index into Content; they are a trust boundary,
    //   so they're clamped rather than passed straight to Substring, where one malformed
    //   span would throw ArgumentOutOfRangeException past every typed error path.
    // - Fast paths: no spans, and the overwhelmingly common single-span page (no sort,
    //   no builder).
    private static string SliceBySpans(string content, IReadOnlyList<DocumentSpan>? spans, ref int truncated)
    {
        if (spans is not { Count: > 0 }) return "";

        if (spans.Count == 1)
            return Clamp(content, spans[0], ref truncated).ToString();

        var ordered = spans.OrderBy(s => s.Offset);
        var builder = new StringBuilder(content.Length < 8192 ? content.Length : 8192);

        foreach (var span in ordered)
            builder.Append(Clamp(content, span, ref truncated));

        return builder.ToString();

        static ReadOnlySpan<char> Clamp(string content, DocumentSpan span, ref int truncated)
        {
            if (span.Offset < 0 || span.Offset >= content.Length)
            {
                truncated++;
                return default;
            }

            var length = Math.Min(span.Length, content.Length - span.Offset);
            if (length < span.Length) truncated++;

            return content.AsSpan(span.Offset, length);
        }
    }

    // Matches a whole "<!-- PageHeader="..." -->"-style line (also PageFooter/PageNumber/
    // FigureContent), anchored to a full line so the same literal text appearing in the
    // document's own prose isn't eaten. The quoted value uses (?:[^"\\]|\\.)* rather than
    // a lazy ".*?" so an escaped quote inside it can't truncate the match early.
    // PageBreak carries no attribute at all (DI emits a bare "<!-- PageBreak -->"), so
    // it's a separate alternative rather than folding an optional value into the
    // "=value" branch - that would let it also match zero-width against any other
    // unattributed comment this regex was never meant to strip.
    [GeneratedRegex(
        @"^[ \t]*<!--\s*(?:(?:Page(?:Header|Footer|Number)|FigureContent)\s*=\s*""(?:[^""\\]|\\.)*""|PageBreak)\s*-->[ \t]*\r?\n?",
        RegexOptions.Multiline)]
    private static partial Regex NoiseCommentLineRegex();

    // DI renders the document Title as setext ("Title" + "===" underline) unlike every
    // other heading, which it renders as ATX.
    // - "=" underlines only: "-" underlines are ambiguous with a thematic break (<hr>).
    // - \r? before $ is load-bearing. .NET's multiline $ anchors immediately before \n,
    //   and [ \t]* can't consume a \r, so without it the pattern silently never matches
    //   CRLF content.
    // - The title group excludes \r as well as \n, so it can't swallow the line's own
    //   carriage return on CRLF input.
    [GeneratedRegex(@"^(?<title>[^\r\n]+)\r?\n=+[ \t]*\r?$", RegexOptions.Multiline)]
    private static partial Regex SetextTitleRegex();

    [GeneratedRegex(@"<table\b", RegexOptions.IgnoreCase)]
    private static partial Regex TableOpenTagRegex();

    [GeneratedRegex(@"</table\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex TableCloseTagRegex();
}
