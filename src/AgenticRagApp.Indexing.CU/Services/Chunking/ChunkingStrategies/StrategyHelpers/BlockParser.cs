namespace AgenticRagApp.Indexing.CU.Services;

// Step 2 of the recursive route: one forward pass that turns the document into blocks.
//
// A single pass, on a RUNNING CURSOR over the original string. The helper this replaces did
// content.Split('\n') and then string.Join("\n", lines) to rebuild each run, which is wrong in
// two ways at once: it rewrites \r\n as \n, and it produces a string that no longer has a
// position in the document. Everything downstream - page attribution, the parent window, the
// slice invariant on ContentPiece - depends on a block being a WINDOW onto the content.
//
// Blocks are contiguous and cover the whole document: block k ends where block k+1 begins, so
// the newline between two runs belongs to the earlier one. Nothing is dropped at parse time;
// whitespace-only blocks are trimmed away later, when pieces are built.
public static class BlockParser
{
    // Start and End are absolute; End excludes the line's own newline.
    private readonly record struct Line(int Start, int End, BlockKind Kind, bool IsBlank);

    public static IReadOnlyList<ContentBlock> Parse(string content)
    {
        if (string.IsNullOrEmpty(content)) return [];

        var lines  = ReadLines(content);
        var runs   = GroupIntoRuns(content, lines);
        var blocks = Slice(content, lines, runs);

        // The line tests only produce CANDIDATES - a run of pipe lines, a run of item lines. The
        // block tests are the authority, so a candidate that does not survive its own detector
        // becomes prose. Running the same detectors the strategy will run means the parser and
        // the strategy can never disagree about what a block is.
        blocks = Confirm(blocks);

        return MergeProse(content, blocks);
    }

    private static List<Line> ReadLines(string content)
    {
        var lines = new List<Line>();
        var start = 0;

        while (true)
        {
            var newline = content.IndexOf('\n', start);
            var end     = newline < 0 ? content.Length : newline;
            var text    = content[start..end];

            lines.Add(new Line(start, end, ClassifyLine(text), string.IsNullOrWhiteSpace(text)));

            if (newline < 0) break;
            start = newline + 1;
        }

        return lines;
    }

    // The strongest structure the LINE shows. Blank lines count as prose so that a paragraph and
    // the blank line after it stay in one run, while a blank line still terminates a table or a
    // list - which is exactly how those runs end in practice.
    private static BlockKind ClassifyLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))   return BlockKind.Prose;
        if (TableDetector.IsRow(line))         return BlockKind.Table;
        if (ListRunDetector.IsItem(line))      return BlockKind.ListRun;

        if (KeyValueDetector.IsPair(line) || KeyValueDetector.IsLabel(line))
            return BlockKind.KeyValue;

        return BlockKind.Prose;
    }

    private static List<(int First, int Last, BlockKind Kind)> GroupIntoRuns(string content, List<Line> lines)
    {
        var runs = new List<(int First, int Last, BlockKind Kind)>();

        for (var i = 0; i < lines.Count; i++)
        {
            if (runs.Count > 0 && Continues(content, lines, i, runs[^1].Kind))
            {
                runs[^1] = (runs[^1].First, i, runs[^1].Kind);
                continue;
            }

            runs.Add((i, i, lines[i].Kind));
        }

        return runs;
    }

    // Same kind continues a run. The one exception is the adjacent-line key-value form: after a
    // bare "Label:", the next line IS the value, and it looks like prose because a value is
    // prose. Closing the run there would put the label and its value in different blocks, which
    // is the one thing the key-value kind exists to prevent.
    private static bool Continues(string content, List<Line> lines, int index, BlockKind runKind)
    {
        if (lines[index].Kind == runKind) return true;

        if (runKind != BlockKind.KeyValue || lines[index].Kind != BlockKind.Prose || lines[index].IsBlank)
            return false;

        var previous = content[lines[index - 1].Start..lines[index - 1].End];

        return KeyValueDetector.IsLabel(previous);
    }

    // A run covers from its first line's start to the next run's first line - so the newlines
    // between runs are accounted for and the blocks tile the document exactly.
    private static List<ContentBlock> Slice(
        string content, List<Line> lines, List<(int First, int Last, BlockKind Kind)> runs)
    {
        var blocks = new List<ContentBlock>(runs.Count);

        for (var i = 0; i < runs.Count; i++)
        {
            var start = lines[runs[i].First].Start;
            var end   = i + 1 < runs.Count ? lines[runs[i + 1].First].Start : content.Length;

            blocks.Add(new ContentBlock(content[start..end], start, runs[i].Kind));
        }

        return blocks;
    }

    private static List<ContentBlock> Confirm(List<ContentBlock> blocks)
    {
        for (var i = 0; i < blocks.Count; i++)
        {
            var confirmed = blocks[i].Kind switch
            {
                BlockKind.Table    => TableDetector.IsTable(blocks[i]),
                BlockKind.ListRun  => ListRunDetector.IsListRun(blocks[i]),
                BlockKind.KeyValue => KeyValueDetector.IsKeyValue(blocks[i]),
                _                  => true,
            };

            if (!confirmed) blocks[i] = blocks[i] with { Kind = BlockKind.Prose };
        }

        return blocks;
    }

    // Prose runs that became adjacent after a demotion are one paragraph flow, not two. Merged
    // by SLICING from the first block's start to the last block's end - never by joining their
    // texts, which would guess at the whitespace between them and lose the coordinates.
    private static List<ContentBlock> MergeProse(string content, List<ContentBlock> blocks)
    {
        var merged = new List<ContentBlock>(blocks.Count);

        foreach (var block in blocks)
        {
            if (merged.Count > 0 && merged[^1].Kind == BlockKind.Prose && block.Kind == BlockKind.Prose)
            {
                var start  = merged[^1].Start;
                merged[^1] = new ContentBlock(content[start..block.End], start, BlockKind.Prose);
                continue;
            }

            merged.Add(block);
        }

        return merged;
    }
}
