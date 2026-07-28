using System.Text;
using AgenticRagApp.Common.Models;

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
//   (SplitIfOversized), then each piece re-enters the same packing loop as any other
//   paragraph - same fallback shape ChunkingStrategy1 uses for one long run of prose.
// • After each flush, the next chunk is seeded with a short sentence-aligned tail of the
//   one just flushed (TakeOverlap), so a fact sitting right at a chunk boundary still
//   appears in both the chunk before and after it - same rationale as ChunkingStrategy1's
//   own overlap step.
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

    private static readonly char[] SentenceEnders = ['.', '!', '?'];

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
            foreach (var piece in SplitIfOversized(para))
            {
                if (current.Length > 0 && current.Length + piece.Length + 2 > _maxSize)
                    Flush(chunks, current);
                else if (current.Length >= _targetSize)
                    Flush(chunks, current);

                if (current.Length > 0) current.Append("\n\n");
                current.Append(piece);
            }
        }

        Flush(chunks, current);
        MergeTinyTrailingChunk(chunks);

        return chunks.Select((text, index) => new TextChunk(index, text)).ToList();
    }

    // Emits whatever's in current as a chunk, then seeds the next chunk with a short
    // sentence-aligned tail of it (TakeOverlap) so content near the boundary isn't only
    // ever on one side of the split.
    private void Flush(List<string> chunks, StringBuilder current)
    {
        if (current.Length == 0) return;

        var text = current.ToString().Trim();
        current.Clear();
        if (text.Length == 0) return;

        chunks.Add(text);

        var overlap = TakeOverlap(text);
        if (overlap.Length > 0)
            current.Append(overlap);
    }

    private void MergeTinyTrailingChunk(List<string> chunks)
    {
        if (chunks.Count < 2 || chunks[^1].Length >= _minTail) return;

        chunks[^2] = $"{chunks[^2]}\n\n{chunks[^1]}";
        chunks.RemoveAt(chunks.Count - 1);
    }

    // Sentence-aligned tail of the last OverlapSize characters of a just-flushed chunk -
    // starts after the first sentence end found in that window, so the overlap begins
    // mid-thought as rarely as possible. Falls back to the raw tail if no sentence
    // boundary exists in the window at all.
    private string TakeOverlap(string text)
    {
        if (text.Length <= _overlapSize) return string.Empty;

        var tail    = text[^_overlapSize..];
        var splitAt = tail.IndexOfAny(SentenceEnders);
        return splitAt >= 0 && splitAt + 1 < tail.Length
            ? tail[(splitAt + 1)..].TrimStart()
            : tail;
    }

    // A paragraph that alone exceeds MaxSize is split on sentence boundaries and repacked
    // greedily. Pieces from here re-enter the caller's normal paragraph-packing loop, so
    // they can still merge with neighboring paragraphs if they end up small.
    private IEnumerable<string> SplitIfOversized(string paragraph)
    {
        if (paragraph.Length <= _maxSize)
        {
            yield return paragraph;
            yield break;
        }

        var sb = new StringBuilder();
        foreach (var sentence in SplitSentences(paragraph))
        {
            if (sb.Length > 0 && sb.Length + sentence.Length + 1 > _maxSize)
            {
                yield return sb.ToString().Trim();
                sb.Clear();
            }
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(sentence);
        }
        if (sb.Length > 0)
            yield return sb.ToString().Trim();
    }

    private static IEnumerable<string> SplitSentences(string text)
    {
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (Array.IndexOf(SentenceEnders, text[i]) >= 0 &&
                (i + 1 == text.Length || char.IsWhiteSpace(text[i + 1])))
            {
                yield return text[start..(i + 1)].Trim();
                start = i + 1;
            }
        }
        if (start < text.Length)
        {
            var rest = text[start..].Trim();
            if (rest.Length > 0) yield return rest;
        }
    }
}
