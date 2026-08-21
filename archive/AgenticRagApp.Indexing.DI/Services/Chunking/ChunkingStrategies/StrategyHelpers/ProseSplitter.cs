namespace AgenticRagApp.Indexing.DI.Services;

// Step 6 of the recursive route: a prose block becomes its paragraphs.
//
// This is the paragraph rung of the ladder, consumed at parse time rather than during cutting.
// Doing it here is what lets BlockPacker work in paragraph units - packing whole paragraphs up
// to the ceiling is the difference between chunks that sit near the ceiling and one 40-token
// chunk per paragraph.
//
// Blank lines separate paragraphs and belong to neither, so no returned block covers them.
// Nothing is lost by that: the packer re-slices from the first paragraph's Start to the last
// one's End, which restores the blank lines in between.
public static class ProseSplitter
{
    public static IReadOnlyList<ContentBlock> SplitParagraphs(ContentBlock block)
    {
        var paragraphs = new List<ContentBlock>();
        var text       = block.Text;

        // Local coordinates throughout - block.Start makes them absolute at the end.
        int? start  = null;
        var  end    = 0;
        var  cursor = 0;

        while (true)
        {
            var newline = text.IndexOf('\n', cursor);
            var lineEnd = newline < 0 ? text.Length : newline;

            if (string.IsNullOrWhiteSpace(text[cursor..lineEnd]))
            {
                // A blank line closes whatever paragraph was open.
                if (start.HasValue) Add(paragraphs, block, start.Value, end);
                start = null;
            }
            else
            {
                start ??= cursor;
                end     = lineEnd;
            }

            if (newline < 0) break;
            cursor = newline + 1;
        }

        if (start.HasValue) Add(paragraphs, block, start.Value, end);

        return paragraphs;
    }

    private static void Add(List<ContentBlock> paragraphs, ContentBlock block, int start, int end)
    {
        var text = block.Text[start..end];
        if (string.IsNullOrWhiteSpace(text)) return;

        paragraphs.Add(new ContentBlock(text, block.Start + start, BlockKind.Prose));
    }
}
