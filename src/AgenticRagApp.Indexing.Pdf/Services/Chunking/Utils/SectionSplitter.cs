using System.Text.RegularExpressions;
using AgenticRagApp.Indexing.Pdf.Services;

namespace AgenticRagApp.Indexing.Pdf.Utils;

// One child piece cut out of a section.
public sealed record SectionPiece(string Text, bool IsTable, bool IsOverlap);

// Cuts one section's text into child pieces, in the corrected cascade order.
//
// The ordering matters and was wrong before. The old formulation listed tables as a
// precedence BRANCH, competing with headings - which left a prose+table+prose section with
// no defined answer, since it is simultaneously both. Tables are not a branch; they are an
// ATOMICITY CONSTRAINT on splitting:
//
//   1. Headings define parents.            (upstream - HeadingLocator)
//   2. Tables are no-cut regions.          the child splitter may not cross one
//   3. A section over the ceiling is sub-split, balanced, paragraph then sentence.
//   4. A table that alone exceeds the ceiling is row-split, header repeated per fragment.
//   5. A document with no headings falls back to one section covering everything.
//                                          (upstream - HeadingLocator)
//
// Overlap applies only to prose sub-splits. A section boundary is a validated topic change,
// so duplicating a tail across one would mix two topics in a single embedding - which is
// exactly what the boundary exists to prevent. Arbitrary cuts inside a section are the case
// overlap was invented for.
public sealed partial class SectionSplitter : ITextSplitter
{
    // Microsoft's starting point is 512 tokens with 25% overlap.
    public const int DefaultTokenCeiling = 512;

    public string Name => "SectionCascadeSplitter";

    // A list item: a bullet or a number, then whitespace. Ordered and unordered both count -
    // what matters is that the run is a sequence of short peer items rather than continuous
    // prose, because that is what makes a mid-item cut so much worse than a mid-paragraph one.
    [GeneratedRegex(@"^\s*([-*•·]|\(?\d{1,3}[.)])\s+\S", RegexOptions.Compiled)]
    private static partial Regex ListItemLine();

    // Phase A measured the median section at 629-977 characters against a ~1,640 ceiling,
    // with 83-87% of sections never needing a second cut at all. So this ceiling is a limit
    // for the long tail, not a target to chunk toward - most sections pass through whole.
    public IReadOnlyList<SectionPiece> Split(string sectionText, int tokenCeiling = DefaultTokenCeiling)
    {
        var pieces = new List<SectionPiece>();
        if (string.IsNullOrWhiteSpace(sectionText)) return pieces;

        foreach (var (isTable, text) in ChunkingHelper.SplitIntoBlocks(sectionText))
        {
            if (string.IsNullOrWhiteSpace(text)) continue;

            if (isTable)
            {
                // Step 4. A header-less run of numbers means nothing to either the embedder
                // or the model, so every fragment repeats the header row. Never overlapped:
                // repeating rows across fragments would duplicate data, not context.
                var budget = ChunkingHelper.CharBudgetForTokens(tokenCeiling, isTable: true);
                foreach (var fragment in ChunkingHelper.ChunkTable(text, budget))
                    pieces.Add(new SectionPiece(fragment.Trim(), IsTable: true, IsOverlap: false));

                continue;
            }

            // A prose block can still contain list runs, and a list wants a different cut
            // rule from continuous prose - so the block is subdivided again before splitting.
            foreach (var (isList, run) in SplitProseAndLists(text))
            {
                if (string.IsNullOrWhiteSpace(run)) continue;

                if (isList) AddListPieces(pieces, run, tokenCeiling);
                else        AddProsePieces(pieces, run, tokenCeiling);
            }
        }

        return pieces;
    }

    // Step 3. Balanced rather than greedy fill-then-remainder: greedy packing produces a runt
    // on any section just over the ceiling, and the overlap seeded into that runt then
    // inflates it past the tiny-tail merge threshold, so it survives as a chunk that is
    // almost entirely a copy of its predecessor.
    private static void AddProsePieces(List<SectionPiece> pieces, string text, int tokenCeiling)
    {
        var budget   = ChunkingHelper.CharBudgetForTokens(tokenCeiling, isTable: false);
        var children = ChunkingHelper.SplitBalanced(text, budget);

        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];

            // Overlap is sized against the PRODUCED child, not the ceiling. Sizing it
            // against the ceiling is what made the runt case degenerate: a 470-character
            // piece was handed 410 characters of overlap.
            if (i > 0)
            {
                var overlap = ChunkingHelper.TakeOverlap(children[i - 1], child.Length / 4);
                if (overlap.Length > 0)
                {
                    pieces.Add(new SectionPiece($"{overlap}\n\n{child}".Trim(), IsTable: false, IsOverlap: true));
                    continue;
                }
            }

            pieces.Add(new SectionPiece(child.Trim(), IsTable: false, IsOverlap: false));
        }
    }

    // The narrow exception where a numeric window is the right tool: a list is a sequence of
    // peer items, so the only sensible cut is between whole items. Balanced character
    // splitting would cut mid-item, and a half-item is worse than an uneven chunk - the
    // reader cannot tell a truncated instruction from a complete one.
    //
    // No overlap: repeating whole items across two chunks duplicates instructions rather than
    // restoring context, and a list item is already self-contained by construction.
    private static void AddListPieces(List<SectionPiece> pieces, string listText, int tokenCeiling)
    {
        var budget = ChunkingHelper.CharBudgetForTokens(tokenCeiling, isTable: false);

        if (listText.Length <= budget)
        {
            pieces.Add(new SectionPiece(listText.Trim(), IsTable: false, IsOverlap: false));
            return;
        }

        var items   = listText.Split('\n');
        var current = new List<string>();
        var length  = 0;

        foreach (var item in items)
        {
            // A single item longer than the budget is kept whole rather than hard-split, for
            // the same reason an oversized table row is: one oversized chunk beats a
            // fragment that reads as a complete item but isn't.
            if (current.Count > 0 && length + item.Length + 1 > budget)
            {
                pieces.Add(new SectionPiece(string.Join("\n", current).Trim(), IsTable: false, IsOverlap: false));
                current.Clear();
                length = 0;
            }

            current.Add(item);
            length += item.Length + 1;
        }

        if (current.Count > 0)
            pieces.Add(new SectionPiece(string.Join("\n", current).Trim(), IsTable: false, IsOverlap: false));
    }

    // Splits a prose block into alternating list and prose runs. A run counts as a list only
    // at two or more consecutive item lines - the same rule SplitIntoBlocks uses for tables,
    // and for the same reason: one line that happens to start with a dash is a sentence with
    // a dash in it, not a list.
    private static List<(bool IsList, string Text)> SplitProseAndLists(string text)
    {
        var runs = new List<(bool IsList, List<string> Lines)>();

        foreach (var line in text.Split('\n'))
        {
            var isItem = ListItemLine().IsMatch(line);
            if (runs.Count > 0 && runs[^1].IsList == isItem)
                runs[^1].Lines.Add(line);
            else
                runs.Add((isItem, [line]));
        }

        for (var i = 0; i < runs.Count; i++)
            if (runs[i].IsList && runs[i].Lines.Count < 2)
                runs[i] = (false, runs[i].Lines);

        // Re-merge prose runs that became adjacent after that demotion.
        var merged = new List<(bool IsList, string Text)>();
        foreach (var (isList, lines) in runs)
        {
            var joined = string.Join("\n", lines);
            if (merged.Count > 0 && !merged[^1].IsList && !isList)
                merged[^1] = (false, merged[^1].Text + "\n" + joined);
            else
                merged.Add((isList, joined));
        }

        return merged;
    }
}
