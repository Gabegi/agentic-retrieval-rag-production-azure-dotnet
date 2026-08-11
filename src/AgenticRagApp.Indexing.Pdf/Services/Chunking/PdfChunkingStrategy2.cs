using AgenticRagApp.Common.Models;
using AgenticRagApp.Indexing.Pdf.Utils;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Strategy 2 — Table-Aware Paragraph Sliding Window
//
// Wraps PdfChunkingStrategy1's paragraph-aware sliding window (packing, sentence-aligned
// overlap, tiny-trailing-chunk merge) for ordinary prose, but treats a markdown table (see
// PdfCleaner's table-to-markdown conversion) as its own unit instead of paragraph/sentence
// text: a table that fits in one chunk is never split, and one too large for a single chunk
// is split row-by-row with its header (and separator row) repeated at the top of every
// continuation chunk - so no chunk of a table ever loses the column meaning its header
// provides. Resolves the open TODO left in PdfChunkingStrategy1's own comment ("NOT
// table-aware... whether/how to combine that logic with this strategy is an open decision").
//
// Block-based, same trade-off the old ChunkingStrategy2 already accepted for
// ChunkingStrategy1: a table interrupts the sliding window, so overlap continuity resets at
// each table boundary rather than carrying across it.
public sealed class PdfChunkingStrategy2 : IChunkingStrategy
{
    private readonly PdfChunkingStrategy1 _proseChunker;
    private readonly int _maxSize;

    public string Name => "TableAwareParagraphSlidingWindow";

    public PdfChunkingStrategy2(int targetSize = 1_000, int maxSize = 1_500, int minTail = 200, int overlapSize = 150)
    {
        _maxSize      = maxSize;
        _proseChunker = new PdfChunkingStrategy1(targetSize, maxSize, minTail, overlapSize);
    }

    public IReadOnlyList<TextChunk> Chunk(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        var chunks = new List<TextChunk>();
        var index  = 0;

        foreach (var block in ChunkingHelper.SplitIntoBlocks(content))
        {
            var pieces = block.IsTable
                ? ChunkingHelper.ChunkTable(block.Text, _maxSize)
                : _proseChunker.Chunk(block.Text).Select(c => c.Content).ToList();

            foreach (var piece in pieces)
            {
                var trimmed = piece.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                    chunks.Add(new TextChunk(index++, trimmed,
                        EstimatedTokens: ChunkingHelper.EstimateTokens(trimmed, isTable: block.IsTable)));
            }
        }

        return chunks;
    }
}
