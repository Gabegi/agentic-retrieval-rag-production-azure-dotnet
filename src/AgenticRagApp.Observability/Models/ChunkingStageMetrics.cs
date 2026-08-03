using AgenticRagApp.Common.Models;
namespace AgenticRagApp.Observability.Reports;

public sealed record ChunkingStageMetrics(
    int    ChunksProduced,
    int    DocsWithZeroChunks,
    int    DuplicateChunks,
    long   MinChunkSizeChars,
    long   MaxChunkSizeChars,
    double AvgChunkSizeChars,
    long   P95ChunkSizeChars,
    int    BandUnder100,
    int    Band100To500,
    int    Band500To1500,
    int    Band1500Plus,
    int    CoherentChunks,
    int    HeadingsDetected,
    string Strategy)
{
    public static ChunkingStageMetrics Empty(string strategy) => new(
        ChunksProduced:     0, DocsWithZeroChunks: 0, DuplicateChunks: 0,
        MinChunkSizeChars:  0, MaxChunkSizeChars:  0, AvgChunkSizeChars: 0, P95ChunkSizeChars: 0,
        BandUnder100:       0, Band100To500: 0, Band500To1500: 0, Band1500Plus: 0,
        CoherentChunks:     0, HeadingsDetected: 0, Strategy: strategy);

    public static ChunkingStageMetrics Compute<T>(IReadOnlyList<T> chunks, string strategy) where T : IChunkStatsSource
    {
        if (chunks.Count == 0) return Empty(strategy);

        var seen         = new HashSet<string>();
        var sizes        = new List<long>(chunks.Count);
        var docsProduced = new HashSet<string>();
        var allDocIds    = new HashSet<string>(chunks.Select(c => c.DocumentId));
        int duplicates = 0, coherent = 0, headings = 0;
        int band0 = 0, band1 = 0, band2 = 0, band3 = 0;

        foreach (var chunk in chunks)
        {
            var len = (long)chunk.Content.Length;
            sizes.Add(len);
            docsProduced.Add(chunk.DocumentId);

            if      (len < 100)  band0++;
            else if (len < 500)  band1++;
            else if (len < 1500) band2++;
            else                 band3++;

            if (!seen.Add(chunk.Content)) duplicates++;

            if (chunk.IsCoherent)      coherent++;
            if (chunk.Heading != null) headings++;
        }

        sizes.Sort();
        var p95Index = (int)(sizes.Count * 0.95);

        return new ChunkingStageMetrics(
            ChunksProduced:     chunks.Count,
            DocsWithZeroChunks: allDocIds.Count - docsProduced.Count,
            DuplicateChunks:    duplicates,
            MinChunkSizeChars:  sizes[0],
            MaxChunkSizeChars:  sizes[^1],
            AvgChunkSizeChars:  sizes.Average(),
            P95ChunkSizeChars:  sizes[Math.Min(p95Index, sizes.Count - 1)],
            BandUnder100:       band0,
            Band100To500:       band1,
            Band500To1500:      band2,
            Band1500Plus:       band3,
            CoherentChunks:     coherent,
            HeadingsDetected:   headings,
            Strategy:           strategy);
    }
}
