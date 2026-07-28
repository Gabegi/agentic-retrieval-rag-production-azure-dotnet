using System.Text.RegularExpressions;
using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Strategy 2 — Table-Aware Sentence Sliding Window
//
// Wraps ChunkingStrategy1's sentence/word-boundary logic for ordinary prose, but treats
// markdown tables as their own unit: a table that fits in one chunk is never split, and a
// table too large for one chunk is split row-by-row with its header row repeated at the top
// of every continuation chunk - so no chunk of a table ever loses the column meaning its
// header provides.
public class ChunkingStrategy2 : IChunkingStrategy
{
    private static readonly Regex TableRowLine = new(@"^\s*\|.*\|\s*$", RegexOptions.Compiled);

    private readonly ChunkingStrategy1 _proseChunker;
    private readonly int _maxChars;

    public string Name => "TableAwareSlidingWindow";

    public ChunkingStrategy2(int maxChars = 1_500, int overlapChars = 150)
    {
        _maxChars     = maxChars;
        _proseChunker = new ChunkingStrategy1(maxChars, overlapChars);
    }

    public IReadOnlyList<TextChunk> Chunk(string content)
    {
        var chunks = new List<TextChunk>();
        var index  = 0;

        foreach (var block in SplitIntoBlocks(content))
        {
            var pieces = block.IsTable
                ? ChunkTable(block.Text, _maxChars)
                : _proseChunker.Chunk(block.Text).Select(c => c.Content).ToList();

            foreach (var piece in pieces)
            {
                var trimmed = piece.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                    chunks.Add(new TextChunk(index++, trimmed));
            }
        }

        return chunks;
    }

    // Splits content into alternating table/prose runs. A block only counts as a table if
    // it's at least 2 consecutive lines matching the markdown table-row shape - a lone line
    // that happens to contain "|" is left as ordinary prose, not treated as a table.
    private static List<(bool IsTable, string Text)> SplitIntoBlocks(string content)
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
    private static List<string> ChunkTable(string tableBlock, int maxChars)
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
