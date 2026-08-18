namespace AgenticRagApp.Indexing.Pdf.Services;

// Lines as RANGES, not as strings.
//
// Every cutter that works line-wise - tables on rows, list runs on items, the line rung of the
// prose ladder - needs to know where a line sits, not just what it says. Split('\n') answers the
// second question and destroys the first, which is the single mistake this whole set of helpers
// exists to avoid.
public static class LineSpans
{
    // End excludes the line's own newline, so text[Start..End] never carries a trailing \n. A
    // trailing \r survives, which is deliberate: the slice has to match the source exactly, and
    // every line test here tolerates it.
    public static List<(int Start, int End)> Read(string text)
    {
        var spans = new List<(int Start, int End)>();
        var start = 0;

        while (true)
        {
            var newline = text.IndexOf('\n', start);
            var end     = newline < 0 ? text.Length : newline;

            spans.Add((start, end));

            if (newline < 0) break;
            start = newline + 1;
        }

        return spans;
    }

    // The lines that carry something. A table run can end on a blank line, and a blank row would
    // otherwise be packed as if it were data.
    public static List<(int Start, int End)> NonBlank(string text) =>
        Read(text)
            .Where(span => !string.IsNullOrWhiteSpace(text[span.Start..span.End]))
            .ToList();
}
