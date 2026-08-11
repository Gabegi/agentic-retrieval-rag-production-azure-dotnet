using System.Text;
using AgenticRagApp.Common.Models;
using AgenticRagApp.Indexing.Pdf.Utils;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Paragraph-Aware Sliding Window
//
// Process:
// • Splits content on blank lines into paragraphs. Content is already \n-normalized by
//   PdfCleaner before it reaches chunking, so a blank line is always exactly "\n\n" here.
// • Greedily packs paragraphs into a chunk until it reaches TargetSize, then flushes - a
//   chunk is allowed to grow past TargetSize (up to MaxSize) if the next paragraph still
//   fits whole; it only flushes early if the next paragraph would push it over MaxSize.
// • A paragraph longer than MaxSize on its own is split on sentence boundaries first
//   (ChunkingHelper.SplitIfOversized), then each piece re-enters the same packing loop as
//   any other paragraph - same fallback shape ChunkingStrategy1 uses for one long run of prose.
// • After each flush, the next chunk is seeded with a short sentence-aligned tail of the
//   one just flushed (ChunkingHelper.TakeOverlap, via Flush), so a fact sitting right at a
//   chunk boundary still appears in both the chunk before and after it - same rationale as
//   ChunkingStrategy1's own overlap step.
// • A trailing chunk shorter than MinTail is folded into the previous chunk rather than
//   standing alone as a near-empty final chunk - even past MaxSize, since a chunk a
//   little over MaxSize is still useful, but an orphaned 10-character final chunk isn't
//   (mirrors ChunkingStrategy2's choice to keep an oversized single table row intact
//   rather than hard-split it). Only ever considered for the *last* chunk - a short
//   paragraph in the middle of a document is real structure, not an artifact to absorb.
//
// NOT table-aware: a markdown table embedded in the content is chunked as ordinary
// paragraph/sentence text here, same as everything else - it can be split mid-row.
// ChunkingStrategy2 solves that for ChunkingStrategy1's sliding window; whether/how to
// combine that logic with this strategy is an open decision, not resolved by this file.
public sealed class PdfChunkingStrategy1 : IChunkingStrategy
{
    public string Name => "ParagraphAwareSlidingWindow";

    private readonly int _targetSize;
    private readonly int _maxSize;
    private readonly int _minTail;
    private readonly int _overlapSize;

    // Constructor-injected (not consts) so tests can use small sizes instead of needing
    // 1500-character fixtures - same reason ChunkingStrategy1/2 take these as parameters.
    public PdfChunkingStrategy1(int targetSize = 1_000, int maxSize = 1_500, int minTail = 200, int overlapSize = 150)
    {
        _targetSize  = targetSize;
        _maxSize     = maxSize;
        _minTail     = minTail;
        _overlapSize = overlapSize;
    }

    public IReadOnlyList<TextChunk> Chunk(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        var paragraphs = content
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0);

        var chunks  = new List<string>();
        var current = new StringBuilder();

        foreach (var para in paragraphs)
        {
            foreach (var piece in ChunkingHelper.SplitIfOversized(para, _maxSize))
            {
                if (current.Length > 0 && current.Length + piece.Length + 2 > _maxSize)
                    ChunkingHelper.Flush(chunks, current, _overlapSize);
                else if (current.Length >= _targetSize)
                    ChunkingHelper.Flush(chunks, current, _overlapSize);

                if (current.Length > 0) current.Append("\n\n");
                current.Append(piece);
            }
        }

        ChunkingHelper.Flush(chunks, current, _overlapSize);
        ChunkingHelper.MergeTinyTrailingChunk(chunks, _minTail);

        // Never table-aware (see this class's own header comment) - every chunk here is
        // estimated at the prose ratio.
        return chunks.Select((text, index) =>
            new TextChunk(index, text, EstimatedTokens: ChunkingHelper.EstimateTokens(text, isTable: false))).ToList();
    }
}
