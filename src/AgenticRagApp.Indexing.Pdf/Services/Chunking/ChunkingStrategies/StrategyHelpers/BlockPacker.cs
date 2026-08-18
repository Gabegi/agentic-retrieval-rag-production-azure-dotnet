namespace AgenticRagApp.Indexing.Pdf.Services;

// Merges consecutive PROSE blocks up to the ceiling, so a document of short paragraphs does not
// become a document of short chunks.
//
// The cutters only ever make things smaller. Without this, a route-2 document with twelve
// 40-token paragraphs produces twelve 40-token chunks: each one embeds badly (a short vector is
// noise-dominated) and top-k fills with fragments of a single document.
//
// SAME KIND ONLY, and prose is the only kind that merges. A table, a key-value run and a list
// run are atomic and stay their own blocks - absorbing a table into the paragraph before it
// would put two different things behind one vector and make the table unfindable as a table.
//
// Merging is a RE-SLICE from the first block's Start to the last block's End, never a join of
// their texts. That keeps the coordinates true and restores the blank lines between paragraphs
// for free, since they sit inside the span even though no paragraph block covers them.
public static class BlockPacker
{
    public static IReadOnlyList<ContentBlock> Pack(
        string content, IReadOnlyList<ContentBlock> blocks, int ceiling)
    {
        var packed = new List<ContentBlock>();

        // Tokens accumulated into the block currently being built. A SUM of the parts rather
        // than a re-count of the merged text: re-tokenizing the whole accumulation on every
        // candidate is quadratic, and the sum is an upper bound on the real count (concatenation
        // can merge tokens across the seam, never add any), so it errs toward smaller chunks.
        var tokens = 0;

        foreach (var block in blocks)
        {
            var blockTokens = TokenEstimator.Estimate(block.Text);

            var mergeable = packed.Count > 0
                         && packed[^1].Kind == BlockKind.Prose
                         && block.Kind      == BlockKind.Prose
                         && tokens + blockTokens <= ceiling;

            if (mergeable)
            {
                var start  = packed[^1].Start;
                packed[^1] = new ContentBlock(content[start..block.End], start, BlockKind.Prose);
                tokens    += blockTokens;
                continue;
            }

            packed.Add(block);
            tokens = blockTokens;
        }

        return packed;
    }
}
