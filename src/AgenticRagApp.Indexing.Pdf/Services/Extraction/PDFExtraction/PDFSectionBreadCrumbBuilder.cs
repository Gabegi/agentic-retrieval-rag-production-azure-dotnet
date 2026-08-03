using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Converts a PDF's flat bookmark/outline list into one breadcrumb string per page, e.g.
// "Chapter 3 > 3.2 Dosage" - the deepest section active on that page, plus its parents.
// Extracted out of the deleted PdfMarkdownExtractor: real PDF outline data and a correct
// stack algorithm are worth keeping for a future chunk-builder to attach per-chunk (not
// per-page, the way the old class did it).
public static class PdfSectionBreadCrumbBuilder
{
    // Steps:
    // 1. Keep only bookmarks with a resolvable page number - others can't be anchored to
    //    a page and would corrupt the walk below. Diagnostics: split into external-file
    //    links vs. unresolvable internal destinations, since both collapse to
    //    PageNumber=null but mean different things to a report reader.
    // 2. Sort by page number, then walk in order maintaining a "stack" of titles indexed
    //    by outline depth (Level):
    //    - Trim the stack to Level before adding, so a new top-level bookmark discards
    //      any deeper sub-sections left over from the previous chapter.
    //    - Push the bookmark's title; join the stack's non-blank entries with " > ".
    //    - A skipped outline level (e.g. Level 2 directly under Level 0) stays blank and
    //      gets filtered out, so it renders as "Chapter 3 > 3.2.1", not "Chapter 3 >  > 3.2.1"
    //      - also counted as a diagnostic, since it usually means a sloppily-authored outline.
    //    - Bookmarks pointing past the last page still take part in this walk (they carry
    //      hierarchy for nothing that follows, but skipping them would desync the stack);
    //      they simply never get assigned to a page in step 3.
    // 3. Walk every page 1..pageCount, assigning whichever breadcrumb was most recently
    //    active as of that page.
    //
    // ASSUMPTION: Bookmark.Level is 0-based (top-level == 0). If the extractor ever emits
    // 1-based levels, every top-level bookmark is miscounted as a level gap and the first
    // stack slot stays permanently blank. Covered by unit test.
    //
    // diagnostics is report/diagnostic material only (see PdfStepDiagnostics) - this
    // method never fails; a bad/sparse outline just produces fewer breadcrumbs.
    public static (Dictionary<int, string> Breadcrumbs, PdfStepDiagnostics Diagnostics) BuildSectionBreadcrumbs(
        IReadOnlyList<Bookmark>? bookmarks, int pageCount, string blobName)
    {
        var warnings = new List<PipelineIssue>();
        void Warn(string message) =>
            warnings.Add(PipelineIssue.Warning(PipelineStage.Metadata, blobName, message));

        if (bookmarks is not { Count: > 0 })
            return ([], new PdfStepDiagnostics(warnings, []));

        var ordered = KeepBookmarksWithPageNumbers(bookmarks, pageCount, Warn);
        if (ordered.Count == 0)
            return ([], new PdfStepDiagnostics(warnings, []));

        var breakpoints = BuildBreakpoints(ordered, Warn);
        var result      = AssignBreadcrumbsToPages(breakpoints, pageCount);

        if (result.Count == 0)
            Warn($"{ordered.Count} bookmark(s) resolved to a page but none produced a breadcrumb - "
                 + $"every one is either past page {pageCount} or has a blank title.");

        return (result, new PdfStepDiagnostics(warnings, []));
    }

    // Keeps only bookmarks with a resolvable page number, warns about everything dropped
    // (split into external-file, embedded-file, and unresolvable-internal - which also
    // covers PdfPig's internal, non-public container nodes, see Bookmark's doc comment -
    // and separately, in-range vs. beyond the document), and returns the survivors sorted
    // by page.
    private static List<Bookmark> KeepBookmarksWithPageNumbers(
        IReadOnlyList<Bookmark> bookmarks, int pageCount, Action<string> warn)
    {
        var unresolvable         = bookmarks.Where(b => b.PageNumber is not > 0).ToList();
        var externalCount        = unresolvable.Count(b => b.IsExternal);
        var embeddedCount        = unresolvable.Count(b => b.IsEmbedded);
        var unresolvableInternal = unresolvable.Count - externalCount - embeddedCount;

        if (unresolvableInternal > 0)
            warn($"{unresolvableInternal} bookmark(s) excluded - no resolvable page.");

        if (externalCount > 0)
            warn($"{externalCount} bookmark(s) excluded - point to an external file, not a page in this document.");

        if (embeddedCount > 0)
            warn($"{embeddedCount} bookmark(s) excluded - point to a file embedded in this document, not a page.");

        // Stable sort matters here: two bookmarks on the same page keep their original
        // outline order (OrderBy is documented as stable) - if it weren't, a page with
        // multiple bookmarks could end up with the wrong one "most recently active".
        var ordered = bookmarks
            .Where(b => b.PageNumber is > 0)
            .OrderBy(b => b.PageNumber)
            .ToList();

        if (ordered.Count == 0)
            return ordered;

        var outOfRange = ordered.Count(b => b.PageNumber > pageCount);
        if (outOfRange > 0)
            warn($"{outOfRange} bookmark(s) point beyond this document's {pageCount} page(s) - never assigned to a breadcrumb.");

        return ordered;
    }

    // Walks the page-ordered bookmarks maintaining a "stack" of titles indexed by outline
    // depth (Level), emitting one breakpoint per bookmark: the full " > "-joined path
    // active from that bookmark's page onward. See the class-level comment for the
    // trim/pad/push mechanics and the out-of-range/0-based-Level assumptions.
    private static List<(int PageNumber, string Path)> BuildBreakpoints(
        IReadOnlyList<Bookmark> ordered, Action<string> warn)
    {
        var stack            = new List<string>();
        var breakpoints      = new List<(int PageNumber, string Path)>();
        var skippedLevelGaps = 0;

        foreach (var bm in ordered)
        {
            // Clamp: a malformed outline can hand us a negative depth, which would throw
            // in RemoveRange. Treat anything below 0 as top-level.
            var level = Math.Max(0, bm.Level);

            if (level > stack.Count) skippedLevelGaps++;

            if (stack.Count > level) stack.RemoveRange(level, stack.Count - level);
            while (stack.Count < level) stack.Add("");
            stack.Add(bm.Title?.Trim() ?? "");

            var path = string.Join(" > ", stack.Where(s => !string.IsNullOrWhiteSpace(s)));
            breakpoints.Add((bm.PageNumber!.Value, path));
        }

        if (skippedLevelGaps > 0)
            warn($"{skippedLevelGaps} bookmark(s) skip an outline level (e.g. Level 2 directly under Level 0) - sloppy outline structure.");

        return breakpoints;
    }

    // Sweeps every page 1..pageCount, assigning whichever breakpoint's path was most
    // recently active as of that page.
    private static Dictionary<int, string> AssignBreadcrumbsToPages(
        IReadOnlyList<(int PageNumber, string Path)> breakpoints, int pageCount)
    {
        var result = new Dictionary<int, string>();

        var breakpointIndex = 0;
        string? current = null;
        for (var pageNum = 1; pageNum <= pageCount; pageNum++)
        {
            while (breakpointIndex < breakpoints.Count && breakpoints[breakpointIndex].PageNumber <= pageNum)
                current = breakpoints[breakpointIndex++].Path;

            if (!string.IsNullOrEmpty(current))
                result[pageNum] = $"_Section: {EscapeMarkdown(current)}_";
        }

        return result;
    }

    // Breadcrumbs are emitted as markdown, so raw titles containing emphasis characters
    // would render as formatting instead of text.
    private static string EscapeMarkdown(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length + 8);

        foreach (var ch in value)
        {
            if (ch is '_' or '*' or '`' or '[' or ']' or '\\') sb.Append('\\');
            sb.Append(ch is '\r' or '\n' ? ' ' : ch);
        }

        return sb.ToString();
    }
}
