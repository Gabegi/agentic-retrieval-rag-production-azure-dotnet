using System.Text.RegularExpressions;

namespace AgenticRagApp.Indexing.DI.Services;

// Step 4 of the recursive route: is this block a key-value run?
//
// The shape route 2 exists for. A document that fails the heading gate is typically a form, a
// cover sheet or a metadata table - "Documentnummer: 4.2.1", "Vastgesteld: 12-03-2024" - and
// those pairs are atomic in the strongest sense in this file: a value separated from its label
// is not degraded, it is unretrievable. Nobody searches for "12-03-2024" alone.
//
// Two written forms, both counted:
//   label and value on ONE line       "Vastgesteld: 12-03-2024"
//   label and value on ADJACENT lines "Vastgesteld:" / "12-03-2024"
public static partial class KeyValueDetector
{
    // "Label: value". The label is bounded at 60 characters and may not contain a pipe: an
    // unbounded label makes any prose sentence containing a colon look like a pair, which would
    // route whole paragraphs to a cutter that refuses to cut.
    [GeneratedRegex(@"^\s*[^:|]{1,60}:\s*\S", RegexOptions.Compiled)]
    private static partial Regex PairLine();

    // "Label:" with nothing after it - the value is on the next line.
    [GeneratedRegex(@"^\s*[^:|]{1,60}:\s*$", RegexOptions.Compiled)]
    private static partial Regex LabelOnlyLine();

    public static bool IsPair(string line)  => PairLine().IsMatch(line);

    public static bool IsLabel(string line) => LabelOnlyLine().IsMatch(line);

    // Every non-blank line has to belong to a pair, and there have to be at least two pairs.
    // One "Let op: ..." line inside a paragraph is prose, and treating it as a key-value block
    // would pull it out of the paragraph it belongs to.
    public static bool IsKeyValue(ContentBlock block)
    {
        var lines = block.NonBlankLines();
        if (lines.Count < 2) return false;

        var pairs = 0;

        for (var i = 0; i < lines.Count; i++)
        {
            if (IsPair(lines[i]))
            {
                pairs++;
                continue;
            }

            // A bare label consumes the following line as its value - which is why that line is
            // not required to look like anything in particular. Anything else disqualifies the
            // block: a run that is only PART key-value is prose with pairs in it.
            if (IsLabel(lines[i]) && i + 1 < lines.Count && !IsLabel(lines[i + 1]))
            {
                pairs++;
                i++;
                continue;
            }

            return false;
        }

        return pairs >= 2;
    }
}
