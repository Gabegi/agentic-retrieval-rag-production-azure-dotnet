using System.Security.Cryptography;
using System.Text;
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
    string Strategy,

    // ── Diagnostics ─────────────────────────────────────────────────────────
    // Computed here, where the chunks are already in memory, rather than recovered later by
    // reading chunking.json back out of pipeline-artifacts. That artifact is whole-corpus and
    // uncapped, so reading it to answer "which documents produced nothing" would mean
    // downloading the entire run's content for a handful of strings. See
    // docs/2608/260807/pipeline-report-content-candidates.md §3.
    //
    // All four are capped - these ride a Durable activity return value (64KB row limit).

    // Documents that went into chunking and came out with nothing - empty/whitespace after
    // cleaning, so they contribute nothing to the index and are silently unsearchable.
    // Requires sourceDocumentIds to be passed to Compute; empty otherwise.
    IReadOnlyList<string>              ZeroChunkDocumentIds,
    // Spread across the size bands, for eyeballing what the strategy actually produced.
    IReadOnlyList<ChunkSample>         SampleChunks,
    // The genuine extremes of this run. A 40-character chunk shown verbatim settles the
    // question of whether the chunker is splitting mid-sentence far faster than BandUnder100 does.
    ChunkSample?                       SmallestChunk,
    ChunkSample?                       LargestChunk,
    IReadOnlyList<DuplicateChunkSample> DuplicateSamples)
{
    // Kept small deliberately - see ChunkSample's comment on the Durable row limit.
    private const int MaxSamples        = 5;
    private const int MaxZeroChunkIds   = 20;
    private const int MaxDuplicates     = 10;
    private const int MaxExcerptChars   = 500;

    public static ChunkingStageMetrics Empty(string strategy) => new(
        ChunksProduced:     0, DocsWithZeroChunks: 0, DuplicateChunks: 0,
        MinChunkSizeChars:  0, MaxChunkSizeChars:  0, AvgChunkSizeChars: 0, P95ChunkSizeChars: 0,
        BandUnder100:       0, Band100To500: 0, Band500To1500: 0, Band1500Plus: 0,
        CoherentChunks:     0, HeadingsDetected: 0, Strategy: strategy,
        ZeroChunkDocumentIds: [], SampleChunks: [], SmallestChunk: null, LargestChunk: null,
        DuplicateSamples: []);

    /// <param name="sourceDocumentIds">
    /// The IDs of the documents *handed to* chunking. Required to compute DocsWithZeroChunks
    /// correctly: this used to be derived from the chunks themselves, which meant it compared a
    /// set against itself and was structurally always 0 - a document that produced no chunks
    /// contributes no chunk to derive its own ID from. Pass null only where the caller genuinely
    /// has no input list, and read DocsWithZeroChunks as "not measured" in that case.
    /// </param>
    public static ChunkingStageMetrics Compute<T>(
        IReadOnlyList<T> chunks,
        string strategy,
        IReadOnlyCollection<string>? sourceDocumentIds = null) where T : IChunkStatsSource
    {
        if (chunks.Count == 0)
        {
            // A run where every document produced nothing is exactly the case worth naming,
            // so the empty path still reports the zero-chunk documents rather than [].
            var allZero = (sourceDocumentIds ?? []).Distinct().OrderBy(id => id, StringComparer.Ordinal).ToList();
            return Empty(strategy) with
            {
                DocsWithZeroChunks   = allZero.Count,
                ZeroChunkDocumentIds = allZero.Take(MaxZeroChunkIds).ToList(),
            };
        }

        var sizes        = new List<long>(chunks.Count);
        var docsProduced = new HashSet<string>(StringComparer.Ordinal);
        // Content -> (occurrences, first chunk seen with it). Keyed on content rather than a
        // hash so the excerpt is available without a second pass; the hash is computed once,
        // only for the capped set actually reported.
        var byContent    = new Dictionary<string, (int Count, T First)>(StringComparer.Ordinal);
        int duplicates = 0, coherent = 0, headings = 0;
        int band0 = 0, band1 = 0, band2 = 0, band3 = 0;

        T? smallest = default, largest = default;
        long smallestLen = long.MaxValue, largestLen = -1;

        foreach (var chunk in chunks)
        {
            var len = (long)chunk.Content.Length;
            sizes.Add(len);
            docsProduced.Add(chunk.DocumentId);

            if      (len < 100)  band0++;
            else if (len < 500)  band1++;
            else if (len < 1500) band2++;
            else                 band3++;

            if (byContent.TryGetValue(chunk.Content, out var existing))
            {
                byContent[chunk.Content] = (existing.Count + 1, existing.First);
                duplicates++;
            }
            else
            {
                byContent[chunk.Content] = (1, chunk);
            }

            if (chunk.IsCoherent)      coherent++;
            if (chunk.Heading != null) headings++;

            if (len < smallestLen) { smallestLen = len; smallest = chunk; }
            if (len > largestLen)  { largestLen  = len; largest  = chunk; }
        }

        sizes.Sort();
        var p95Index = (int)(sizes.Count * 0.95);

        // Documents that entered chunking but produced nothing. Falls back to the old
        // (always-zero) behaviour when no input list was supplied, rather than guessing.
        var zeroChunkIds = sourceDocumentIds is null
            ? []
            : sourceDocumentIds.Distinct(StringComparer.Ordinal)
                               .Where(id => !docsProduced.Contains(id))
                               .OrderBy(id => id, StringComparer.Ordinal)
                               .ToList();

        return new ChunkingStageMetrics(
            ChunksProduced:     chunks.Count,
            DocsWithZeroChunks: zeroChunkIds.Count,
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
            Strategy:           strategy,

            ZeroChunkDocumentIds: zeroChunkIds.Take(MaxZeroChunkIds).ToList(),
            SampleChunks:         BuildBandSamples(chunks),
            SmallestChunk:        ToSample(smallest),
            LargestChunk:         ToSample(largest),
            DuplicateSamples:     byContent
                                      .Where(kv => kv.Value.Count > 1)
                                      .OrderByDescending(kv => kv.Value.Count)
                                      .Take(MaxDuplicates)
                                      .Select(kv => new DuplicateChunkSample(
                                          ContentHash:    Sha256(kv.Key),
                                          Occurrences:    kv.Value.Count,
                                          ContentExcerpt: Excerpt(kv.Key),
                                          Truncated:      kv.Key.Length > MaxExcerptChars))
                                      .ToList());
    }

    // One chunk per size band where that band is non-empty, then filled out from the largest
    // remaining band. Sampling across bands rather than taking the first N means the samples
    // represent the distribution instead of whatever happened to sort first.
    private static IReadOnlyList<ChunkSample> BuildBandSamples<T>(IReadOnlyList<T> chunks)
        where T : IChunkStatsSource
    {
        static int Band(int len) => len < 100 ? 0 : len < 500 ? 1 : len < 1500 ? 2 : 3;

        var picked = chunks
            .GroupBy(c => Band(c.Content.Length))
            .OrderBy(g => g.Key)
            .Select(g => g.First())
            .Take(MaxSamples)
            .ToList();

        if (picked.Count < MaxSamples)
        {
            var already = new HashSet<string>(picked.Select(c => c.Id), StringComparer.Ordinal);
            picked.AddRange(chunks.Where(c => !already.Contains(c.Id)).Take(MaxSamples - picked.Count));
        }

        return picked.Select(c => ToSample(c)!).ToList();
    }

    private static ChunkSample? ToSample<T>(T? chunk) where T : IChunkStatsSource =>
        chunk is null ? null : new ChunkSample(
            DocumentId:     chunk.DocumentId,
            PageNumber:     chunk.PageNumber,
            ChunkIndex:     chunk.ChunkIndex,
            Heading:        chunk.Heading,
            SizeChars:      chunk.Content.Length,
            ContentExcerpt: Excerpt(chunk.Content),
            Truncated:      chunk.Content.Length > MaxExcerptChars);

    private static string Excerpt(string content) =>
        content.Length <= MaxExcerptChars ? content : content[..MaxExcerptChars] + "…";

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
