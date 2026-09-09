using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

// Is this document table-shaped? A REPORTED signal, not a routing input - TableChecker stopped
// influencing the route with the two-strategy collapse (D113); atomicity is the splitter's job.
//
// The rule is dominance, not count: at least half the document's characters live in tables. A
// count is absolute where the property is relative - 3 tables in 10,000 pages is a prose ocean
// with three islands, exactly the mistake SectionChecker's density rule fixes for headings.
//
// MEASURED OFF THE TYPED SPANS since 2026-09-09: the characters Content Understanding itself
// says are tables (TableInfo.Offset/Length), which are the same characters BlockParser turns into
// table blocks and TableCutter cuts. Before that it re-parsed the content through BlockParser's
// regex detection (2026-09-08), and before THAT it read a DocumentProfile share that was null on
// every document under CU, so a count-based fallback decided every row - a signal wrong in both
// directions. There is no fallback and no threshold on a count: a table with no span (a blob
// extracted before Length was mapped) contributes nothing, which reads as "not table-shaped",
// the honest answer for data that is absent.
public static class TableChecker
{
    // "Most of the document is table." Self-describing per-document measure - no corpus anchor
    // needed, unlike the count threshold it replaces.
    public const double MinTableCharShare = 0.5;

    // Fraction of the document's characters living in typed table spans - markup included,
    // because the markup is part of what a table chunk costs.
    public static double TableCharShare(string? content, IReadOnlyList<TableInfo> tables)
    {
        if (string.IsNullOrEmpty(content)) return 0;

        var tableChars = tables.Sum(t => t.Length ?? 0);

        return Math.Min(1.0, (double)tableChars / content.Length);
    }

    public static bool IsTableShaped(string? content, IReadOnlyList<TableInfo> tables) =>
        TableCharShare(content, tables) >= MinTableCharShare;
}
