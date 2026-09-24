using AgenticRagApp.Indexing.CU.Models;

namespace AgenticRagApp.Indexing.CU.Services;

// Where the cutter cut, read off the kept chunks (2026-09-23, D224 A6).
//
// This replaces CoherentChunks. That was a proxy - "first character is a capital or digit, last
// character is sentence punctuation" - and on Content Understanding markdown it measured the
// markdown: a whole-section chunk opens with its own "## heading" line and a page-turn chunk
// opens with "<!--", so 85% of bodies failed it by construction and the number read 4.0% on run
// 260922/2 whatever the cutter did. Repairing the proxy would have meant inventing a whitelist of
// acceptable openers (tables, bullets, figure markdown, fences). The cutter already records the
// fact the proxy was guessing at: every piece carries the BoundaryLevel it was cut on.
//
// What each bucket says (BoundaryLevel's own contract):
//   None            - fitted whole, or split by BlockPacker at a paragraph boundary. Both clean.
//                     Not split further here because the packer does not tag which it was, and
//                     deriving it would be new logic in a counter.
//   Paragraph       - blank-line boundary; rarely labelled (consumed at parse time).
//   Sentence, TableRow, ListItem, DiagramElement - clean by construction.
//   Line            - in CU markdown a single newline inside a paragraph is the PDF's visual
//                     wrap, so a Line cut can land mid-sentence (D223 F4). Since A4 put the
//                     sentence rung FIRST, this level is only reached by text the sentence rung
//                     could not cut - text with no usable ender - so on the 2026-09-23 forced run
//                     all 204 Line pieces ended without punctuation. The companion counter that
//                     measured that (LineCutsEndingMidSentence) was retired 2026-09-24, D234 E2:
//                     post-A4 it equals this bucket by construction and adds nothing. Pre-A4 it
//                     did discriminate (626 of 699 on the 260922/2 recount), which is the same
//                     mechanism seen from the other side.
//   Word, HardCut   - mid-sentence by construction. HardCut has its own tripwire in
//                     ChunkingService; "HardCut 0" here is that tripwire's baseline made visible.
//
// EVERY level is emitted, in enum order, zeros included. An absent row cannot be told from a
// level nobody counted; a written 0 can.
public static class CutBoundaryCounters
{
    public static IReadOnlyDictionary<string, int> Of(IReadOnlyList<ChunkObject> chunks)
    {
        // Dictionary rather than SortedDictionary on purpose: System.Text.Json writes insertion
        // order, and enum order is the order the ladder descends, which is how the row reads.
        var buckets = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var level in Enum.GetValues<BoundaryLevel>())
            buckets[level.ToString()] = 0;

        foreach (var chunk in chunks)
            buckets[chunk.BoundaryLevel.ToString()]++;

        return buckets;
    }
}
