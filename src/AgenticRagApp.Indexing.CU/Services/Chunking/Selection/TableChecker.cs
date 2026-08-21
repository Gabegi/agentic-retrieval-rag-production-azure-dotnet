using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

// Step 4 of DetermineStrategy: is this document table-shaped?
//
// The rule is dominance, not count: at least half the document's characters live in table
// blocks (TableCharShare, measured at extraction on the same block split chunking cuts on).
// A count is absolute where the property is relative - 3 tables in 10,000 pages is a prose
// ocean with three islands, exactly the mistake SectionChecker's density rule fixes for
// headings. Direction note: HeadingBased is the primary branch so its floor sits far below
// normal (a tripwire); TableAware is the narrow exception, so its bar sits high - a
// document earns it by being mostly table (the rate-list shape: 1 heading, 5 tables, no
// prose skeleton).
public static class TableChecker
{
    // "Most of the document is table." Self-describing per-document measure - no corpus
    // anchor needed, unlike the count threshold it replaces.
    public const double MinTableCharShare = 0.5;

    // Only used when no profile exists to carry the measured share. Old snapshots
    // deserialize TableCharShare as 0 and route not-table-shaped (the safe default) until
    // re-extraction - they do NOT take this fallback.
    public const int MinTablesFallback = 3;

    public static bool IsTableShaped(int tableCount, DocumentProfile? profile) =>
        profile is null
            ? tableCount >= MinTablesFallback
            : profile.TableCharShare >= MinTableCharShare;
}
