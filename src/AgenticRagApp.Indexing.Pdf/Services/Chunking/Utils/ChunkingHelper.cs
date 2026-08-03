using System.Text;
using System.Text.RegularExpressions;

namespace AgenticRagApp.Indexing.Pdf.Utils;

public static class ChunkingHelper
{
    private static readonly Regex TableRowLine = new(@"^\s*\|.*\|\s*$", RegexOptions.Compiled);

    private static readonly char[] SentenceEnders = ['.', '!', '?'];

    public static string SafeKey(string blobName, int index) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{blobName}::{index}"))
            .Replace('+', '-').Replace('/', '_');

    // Splits on sentence-ending punctuation followed by whitespace or end-of-string.
    public static IEnumerable<string> SplitSentences(string text)
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

    // Emits whatever's in current as a chunk, then seeds the next chunk with a short
    // sentence-aligned tail of it (TakeOverlap) so content near the boundary isn't only
    // ever on one side of the split.
    public static void Flush(List<string> chunks, StringBuilder current, int overlapSize)
    {
        if (current.Length == 0) return;

        var text = current.ToString().Trim();
        current.Clear();
        if (text.Length == 0) return;

        chunks.Add(text);

        var overlap = TakeOverlap(text, overlapSize);
        if (overlap.Length > 0)
            current.Append(overlap);
    }

    public static void MergeTinyTrailingChunk(List<string> chunks, int minTail)
    {
        if (chunks.Count < 2 || chunks[^1].Length >= minTail) return;

        chunks[^2] = $"{chunks[^2]}\n\n{chunks[^1]}";
        chunks.RemoveAt(chunks.Count - 1);
    }

    // Sentence-aligned tail of the last overlapSize characters of a just-flushed chunk -
    // starts after the first sentence end found in that window, so the overlap begins
    // mid-thought as rarely as possible. Falls back to the raw tail if no sentence
    // boundary exists in the window at all.
    public static string TakeOverlap(string text, int overlapSize)
    {
        if (text.Length <= overlapSize) return string.Empty;

        var tail    = text[^overlapSize..];
        var splitAt = tail.IndexOfAny(SentenceEnders);
        return splitAt >= 0 && splitAt + 1 < tail.Length
            ? tail[(splitAt + 1)..].TrimStart()
            : tail;
    }

    // A paragraph that alone exceeds maxSize is split on sentence boundaries and repacked
    // greedily. Pieces from here re-enter the caller's normal paragraph-packing loop, so
    // they can still merge with neighboring paragraphs if they end up small.
    public static IEnumerable<string> SplitIfOversized(string paragraph, int maxSize)
    {
        if (paragraph.Length <= maxSize)
        {
            yield return paragraph;
            yield break;
        }

        var sb = new StringBuilder();
        foreach (var sentence in SplitSentences(paragraph))
        {
            if (sb.Length > 0 && sb.Length + sentence.Length + 1 > maxSize)
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

    // Splits content into alternating table/prose runs. A block only counts as a table if
    // it's at least 2 consecutive lines matching the markdown table-row shape - a lone line
    // that happens to contain "|" is left as ordinary prose, not treated as a table.
    public static List<(bool IsTable, string Text)> SplitIntoBlocks(string content)
    {
        var raw = new List<(bool IsTable, List<string> Lines)>();

        foreach (var line in content.Split('\n'))
        {
            var isTableLine = TableRowLine.IsMatch(line);
            if (raw.Count > 0 && raw[^1].IsTable == isTableLine)
                raw[^1].Lines.Add(line);
            else
                raw.Add((isTableLine, [line]));
        }

        // Demote a lone matching line back to prose - a real table needs at least 2 lines
        // (header + a separator or data row).
        for (int i = 0; i < raw.Count; i++)
            if (raw[i].IsTable && raw[i].Lines.Count < 2)
                raw[i] = (false, raw[i].Lines);

        // Re-merge any prose runs that are now adjacent after that demotion.
        var blocks = new List<(bool IsTable, string Text)>();
        foreach (var (isTable, lines) in raw)
        {
            var text = string.Join("\n", lines);
            if (blocks.Count > 0 && !blocks[^1].IsTable && !isTable)
                blocks[^1] = (false, blocks[^1].Text + "\n" + text);
            else
                blocks.Add((isTable, text));
        }

        return blocks;
    }

    // A table that fits in one chunk is returned whole. Otherwise it's split row-by-row,
    // repeating the header (and separator row, if present) at the top of every continuation
    // chunk. A single data row that alone exceeds maxChars is kept intact rather than
    // hard-split - cutting mid-row would corrupt column alignment, worse than one oversized chunk.
    public static List<string> ChunkTable(string tableBlock, int maxChars)
    {
        if (tableBlock.Length <= maxChars)
            return [tableBlock];

        var lines       = tableBlock.Split('\n');
        var headerCount = lines.Length > 1 && LooksLikeSeparatorRow(lines[1]) ? 2 : 1;
        var header      = lines[..headerCount];
        var dataRows    = lines[headerCount..];

        var chunks     = new List<string>();
        var current    = new List<string>(header);
        var currentLen = string.Join("\n", header).Length;

        foreach (var row in dataRows)
        {
            if (current.Count > headerCount && currentLen + 1 + row.Length > maxChars)
            {
                chunks.Add(string.Join("\n", current));
                current    = [.. header, row];
                currentLen = string.Join("\n", header).Length + 1 + row.Length;
            }
            else
            {
                current.Add(row);
                currentLen += 1 + row.Length;
            }
        }

        if (current.Count > headerCount)
            chunks.Add(string.Join("\n", current));

        return chunks;
    }

    // A GFM separator row looks like "|---|:---:|---:|" - every cell between pipes contains
    // only dashes, colons, and whitespace.
    private static bool LooksLikeSeparatorRow(string line)
    {
        var cells = line.Split('|', StringSplitOptions.RemoveEmptyEntries);
        return cells.Length > 0 && cells.All(cell => cell.Trim().Length > 0 && cell.Trim().All(c => c is '-' or ':'));
    }
}
