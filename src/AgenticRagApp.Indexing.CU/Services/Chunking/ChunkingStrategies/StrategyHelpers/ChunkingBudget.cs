namespace AgenticRagApp.Indexing.CU.Services;

// The token budget every cut is made against, in one place.
//
// It is shared rather than duplicated per strategy because the run report reads it too: a row
// that says "chunks above ceiling" has to mean the same ceiling the cut was actually budgeted
// against, and a second copy is how those two quietly come to disagree. That is not
// hypothetical - this constant previously lived on the old SectionSplitter, and the report was
// reading it from there while the strategies each carried their own private 512.
public static class ChunkingBudget
{
    // Microsoft's starting point, and what the whole prefix-before-cut ordering is budgeted
    // against: the ceiling governs the EMBEDDED text, prefix included. That is why a strategy
    // prices the prefix BEFORE cutting rather than appending it after - the carry-along has to
    // be charged against this number, not added on top of it.
    public const int TokenCeiling = 512;

    // The floor the body keeps no matter how expensive the prefix got. A deep heading chain on a
    // long title can price the body down to nothing; below this a chunk stops being worth
    // retrieving at all, so the ceiling is breached BY CHOICE instead - which is what Degraded
    // records.
    public const int MinBodyTokenBudget = 128;
}
