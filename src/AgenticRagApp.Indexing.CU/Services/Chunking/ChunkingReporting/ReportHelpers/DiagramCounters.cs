using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

// The three diagram numbers of the run report (D214 §2.8), read off one document's kept chunks.
//
// Written once and called from two places - the per-document row (DocumentRowBuilder) and the
// run totals (ChunkingRunState.Chunked, which ChunkingService stamps onto ChunkingStageMetrics
// the way it stamps the two dropped-chunk counts) - so the row and the total cannot disagree
// about what a "cut block" is.
//
// What each answers:
//   Blocks                  - fenced diagrams the cascade cut AS diagrams, whole or in pieces.
//                             Not the fences in the content: a fence straddling a section window
//                             went to prose and is not one (ChunkMetadataBuilder 3h).
//   Cut                     - of those, how many needed more than one fragment. Measured
//                             expectation on run 260921/1: 74 of 718 corpus-wide.
//   FragmentsWithoutContext - cut fragments whose fence matched no figure, so their prefix
//                             carries no caption or description. The 17-of-74 gap; the number
//                             that says whether the 82 payloads that differ by escaping are
//                             worth normalising.
public static class DiagramCounters
{
    public static (int Blocks, int Cut, int FragmentsWithoutContext) Of(IReadOnlyList<ChunkObject> chunks)
    {
        var byFence = chunks
            .Where(c => c.DiagramFence is not null)
            .GroupBy(c => c.DiagramFence!.Value)
            .ToList();

        return (
            Blocks:                  byFence.Count,
            Cut:                     byFence.Count(g => g.Any(c => c.BoundaryLevel == BoundaryLevel.DiagramElement)),
            FragmentsWithoutContext: chunks.Count(c => c.BoundaryLevel == BoundaryLevel.DiagramElement && c.DiagramContextMissing));
    }
}
