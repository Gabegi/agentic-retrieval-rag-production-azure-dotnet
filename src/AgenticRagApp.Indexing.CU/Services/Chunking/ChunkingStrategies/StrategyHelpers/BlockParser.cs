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
//
// TABLES ARE TYPED, NOT DETECTED (2026-09-09). The caller passes the ranges Content
// Understanding reported as tables (DocumentTable.Span, carried as TableInfo.Offset/Length);
// every line overlapping such a range is that table's, whatever it looks like, and nothing else
// is a table however much it looks like one. This replaced a regex line test for `<table>`
// markup (and, before 2026-09-08, for GFM pipe rows that CU never emits) - the same range was
// being found by the service and then found again here. Lists and key-value runs are still
// detected: CU types neither.
public static class BlockParser
{
    // Start and End are absolute; End excludes the line's own newline. Table is the index of the
    // typed table this line belongs to, or -1.
    private readonly record struct Line(int Start, int End, BlockKind Kind, bool IsBlank, int Table);

    public static IReadOnlyList<ContentBlock> Parse(
        string content, IReadOnlyList<(int Start, int End)> tables)
    {
        if (string.IsNullOrEmpty(content)) return [];

        var lines  = ReadLines(content, tables);
        var runs   = GroupIntoRuns(content, lines);
        var blocks = Slice(content, lines, runs);

        // The line tests only produce CANDIDATES - a run of item lines, a run of pair lines. The
        // block tests are the authority, so a candidate that does not survive its own detector
        // becomes prose. Running the same detectors the strategy will run means the parser and
        // the strategy can never disagree about what a block is. Tables need no confirmation:
        // the service said so.
        blocks = Confirm(blocks);

        return MergeProse(content, blocks);
    }

    private static List<Line> ReadLines(string content, IReadOnlyList<(int Start, int End)> tables)
    {
        var lines = new List<Line>();
        var start = 0;

        while (true)
        {
            var newline = content.IndexOf('\n', start);
            var end     = newline < 0 ? content.Length : newline;
            var text    = content[start..end];

            var table = TableAt(tables, start, end);
            var kind  = table >= 0 ? BlockKind.Table : ClassifyLine(text);

            lines.Add(new Line(start, end, kind, string.IsNullOrWhiteSpace(text), table));

            if (newline < 0) break;
            start = newline + 1;
        }

        return lines;
    }

    // The typed table whose span overlaps this line, or -1. A blank line INSIDE a span is still
    // the table's (CU writes multi-line tables), which is why an empty line is tested as a
    // one-character range rather than as nothing.
    private static int TableAt(IReadOnlyList<(int Start, int End)> tables, int start, int end)
    {
        var probeEnd = Math.Max(end, start + 1);

        for (var i = 0; i < tables.Count; i++)
            if (tables[i].Start < probeEnd && tables[i].End > start)
                return i;

        return -1;
    }

    // The strongest structure the LINE shows, for the kinds that are detected. Blank lines count
    // as prose so that a paragraph and the blank line after it stay in one run, while a blank
    // line still terminates a list - which is exactly how those runs end in practice.
    private static BlockKind ClassifyLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))   return BlockKind.Prose;
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
                runs[^1] = (runs[^1].First, i, runs[^1].Kind);
            else
                runs.Add((i, i, lines[i].Kind));
        }

        return runs;
    }

    // Same kind continues a run, with two exceptions.
    //
    // A table run is ONE typed table: two tables back to back are two blocks (TableCutter closes
    // and repeats the markup of one table, not two), so a table line joins the run only when it
    // belongs to the same typed span as the line before it.
    //
    // The adjacent-line key-value form: after a bare "Label:", the next line IS the value, and
    // it looks like prose because a value is prose. Closing the run there would put the label
    // and its value in different blocks, which is the one thing the key-value kind exists to
    // prevent.
    private static bool Continues(string content, List<Line> lines, int index, BlockKind runKind)
    {
        if (runKind == BlockKind.Table || lines[index].Kind == BlockKind.Table)
            return runKind == BlockKind.Table
                && lines[index].Kind == BlockKind.Table
                && lines[index].Table == lines[index - 1].Table;

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
