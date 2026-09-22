using System.Text.RegularExpressions;

namespace AgenticRagApp.Indexing.CU.Services;

// The geometry of a fenced diagram block, for BlockParser and DiagramCutter: where the fences
// are, where a fence's body is, and where the body may be cut. Positions rather than
// substrings, for the same reason TableMarkup gives positions - every fragment's coordinates
// have to keep addressing the characters it carries (the slice invariant on ContentPiece).
//
// THE FENCE IS THE DELIMITER (2026-09-22, D214 §2.2). Content Understanding writes every
// recognised diagram into the markdown as a fenced block - ` ```mermaid ` for a diagram,
// ` ```chart ` for a chart - and the fence is markup the writer put there, on the same footing
// as `<tr>`: not a typed span like a table (CU reports no span for the fence) and not a
// detected shape like a list (nothing here looks at what the body contains to decide whether
// it is a diagram). The rule is CommonMark's: an opener of three or more backticks with an
// optional info string, a closer of at least as many backticks and nothing else, up to three
// spaces of indent on either. An unclosed fence is not reported - the cutter can only cut on a
// boundary the markup declares, the same rule TableMarkup.Rows applies to a row without `</tr>`.
//
// THE INFO STRING IS IGNORED. Measured on run 260921/1 (D214 §1): the same ` ```mermaid ` tag
// carries Mermaid DSL (334 blocks), flowchart JSON (302) and one Graphviz digraph, and all 74
// ` ```chart ` blocks are JSON. The tag says who wrote the payload; the body's first character
// says how it may be cut.
//
// TWO SHAPES, NO KEYWORD LIST (D214 §2.5). A body whose first non-blank character is `{` or
// `[` is cut after every `},` and `],` - the end of a JSON container followed by a comma, the
// seam between two nodes, two edges, two datasets. Anything else is cut at line starts. Both
// are structural boundaries of the text itself; neither needs to know what a Mermaid keyword
// is. Measured: the largest JSON container in the corpus is 1,563 characters and the longest
// non-JSON line is 415, so nothing needs a finer cut.
public static partial class DiagramMarkup
{
    // A fence line: up to three spaces, three or more backticks, then (opener) an info string
    // with no backtick in it, or (closer) nothing but whitespace. A closer also matches the
    // opener shape - which is right: outside a fence, a bare ``` line OPENS one.
    [GeneratedRegex(@"^ {0,3}(`{3,})([^`\r\n]*)\r?$", RegexOptions.Compiled)]
    private static partial Regex OpenerLine();

    [GeneratedRegex(@"^ {0,3}(`{3,})[ \t]*\r?$", RegexOptions.Compiled)]
    private static partial Regex CloserLine();

    // A JSON container's closing bracket followed by a comma. The boundary is the index just
    // PAST the comma, so a segment ends `},` and the next begins with the following element.
    [GeneratedRegex(@"[}\]],", RegexOptions.Compiled)]
    private static partial Regex ContainerComma();

    // The (Start, End) of every COMPLETE fence in the text, in order: Start is the opener
    // line's first character, End is the closer line's last character (its newline excluded,
    // the same convention as LineSpans). Nested or overlapping fences do not exist in
    // CommonMark - a fence runs to the first qualifying closer - so the ranges are disjoint.
    public static IReadOnlyList<(int Start, int End)> Fences(string text)
    {
        var fences = new List<(int Start, int End)>();
        if (string.IsNullOrEmpty(text)) return fences;

        int? openStart = null;
        var  openTicks = 0;

        foreach (var (start, end) in LineSpans.Read(text))
        {
            var line = text[start..end];

            if (openStart is null)
            {
                var opener = OpenerLine().Match(line);
                if (!opener.Success) continue;

                openStart = start;
                openTicks = opener.Groups[1].Length;
                continue;
            }

            var closer = CloserLine().Match(line);
            if (!closer.Success || closer.Groups[1].Length < openTicks) continue;

            fences.Add((openStart.Value, end));
            openStart = null;
        }

        return fences;
    }

    // The body of a fence: from the character after the opener line's newline up to the
    // closer line's start. The body therefore ends with the newline that precedes the closer,
    // which is harmless - boundaries are positions inside it and PieceFactory trims by moving
    // bounds. Empty (Start == End) for a fence with nothing between its lines.
    public static (int Start, int End) Body(string text, (int Start, int End) fence)
    {
        var openerEnd = text.IndexOf('\n', fence.Start);
        var closerStart = text.LastIndexOf('\n', Math.Max(fence.End - 1, fence.Start)) + 1;

        if (openerEnd < 0 || openerEnd + 1 > closerStart) return (closerStart, closerStart);

        return (openerEnd + 1, closerStart);
    }

    // Where the body may be cut, as indices into `text` in ASCENDING order - the shape
    // SpanCutter.Between takes. JSON bodies cut after container commas; everything else cuts
    // at the start of each non-blank line after the first. The opener and closer lines are
    // never cut points: they lie outside the body by construction.
    public static IEnumerable<int> Boundaries(string text, int bodyStart, int bodyEnd)
    {
        if (bodyEnd <= bodyStart) return [];

        var body = text[bodyStart..bodyEnd];

        return IsJson(body)
            ? ContainerComma().Matches(body).Select(m => bodyStart + m.Index + m.Length).ToList()
            : LineSpans.NonBlank(body).Skip(1).Select(span => bodyStart + span.Start).ToList();
    }

    // The first non-blank character decides. `{` is every flowchart and chart payload CU
    // writes; `[` is included because a JSON array is the other top-level container and costs
    // nothing to honour.
    public static bool IsJson(string body)
    {
        foreach (var c in body)
        {
            if (char.IsWhiteSpace(c)) continue;
            return c is '{' or '[';
        }

        return false;
    }
}
