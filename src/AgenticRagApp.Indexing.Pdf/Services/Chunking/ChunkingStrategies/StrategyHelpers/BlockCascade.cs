using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// The cutting cascade, shared by both routes. Give it a window of the content and a ceiling;
// it gives back pieces that all fit.
//
// It lived inside RecursiveStrategy until route 1 needed it too. Route 1's oversized sections
// (the 13-17% the fit gate rejects) have exactly the same problem route 2's whole document
// has - text that must be cut somewhere, and boundaries of differing quality to cut on - so
// duplicating the ladder would have meant two places to keep in step about what a table is.
// ContentPiece's own comment already assumed this shape: "Both routes produce these and
// nothing else."
//
// The two routes differ in WHAT they hand in, not in how it is cut. Route 2 passes the whole
// document and gets flat chunks; route 1 passes one section and keeps that section's heading
// prefix on every piece. Neither difference belongs in here.
//
// The order is a cascade of decreasing structure. A block is classified once, by the strongest
// structure it shows, and the classification decides how it may be cut: a table is cut on rows,
// a key-value run on pairs, a list on items, and only prose falls through to the length cascade
// in CutToCeiling. Prose is the LAST answer, not the first, because a mid-row or mid-pair cut
// destroys information a mid-paragraph cut only interrupts.
public static class BlockCascade
{
    // start/end are a window into content, in content's own coordinates - so every piece that
    // comes back indexes into the SAME string the caller passed, not into the window. Route 2
    // passes the whole document; route 1 passes one section.
    public static IReadOnlyList<ContentPiece> Cut(string content, int start, int end, int ceiling)
    {
        var pieces   = new List<ContentPiece>();
        var proseRun = new List<ContentBlock>();

        // 1. Parse the window into blocks - the units the classification cascade runs on.
        foreach (var block in BlockParser.Parse(content, start, end))
        {
            // 2. Table? A run of consecutive pipe-markdown lines with a header row and a
            //    separator row. Cut on row boundaries, header repeated per fragment.
            if (TableDetector.IsTable(block))
            {
                FlushProse(content, proseRun, pieces, ceiling);
                pieces.AddRange(TableCutter.Cut(block, ceiling));
                continue;
            }

            // 3. Key-value? A label and its value on one line, or on adjacent lines. Cut
            //    between whole pairs - a value separated from its label is unretrievable.
            if (KeyValueDetector.IsKeyValue(block))
            {
                FlushProse(content, proseRun, pieces, ceiling);
                pieces.AddRange(KeyValueCutter.Cut(block, ceiling));
                continue;
            }

            // 4. List run? Two or more consecutive lines matching ListItemLine(). Cut between
            //    whole items - a truncated item reads as a complete one.
            if (ListRunDetector.IsListRun(block))
            {
                FlushProse(content, proseRun, pieces, ceiling);
                pieces.AddRange(ListRunCutter.Cut(block, ceiling));
                continue;
            }

            // 5. Prose only. Split at blank lines into paragraphs and hold them: a paragraph is
            //    not a chunk on its own, it is one unit the packer may merge.
            proseRun.AddRange(ProseSplitter.SplitParagraphs(block));
        }

        // The last prose run has no atomic block after it to close it.
        FlushProse(content, proseRun, pieces, ceiling);

        return pieces;
    }

    // Packing is why prose cannot be emitted paragraph by paragraph inside the loop: consecutive
    // paragraphs merge up to the ceiling, and it is an ATOMIC BLOCK that closes the run. A table
    // between two paragraphs is a real separation - the text on either side of it is not
    // adjacent, and merging across it would put the before and after of a table in one chunk
    // with the table itself in another.
    private static void FlushProse(
        string content, List<ContentBlock> proseRun, List<ContentPiece> pieces, int ceiling)
    {
        if (proseRun.Count == 0) return;

        foreach (var unit in BlockPacker.Pack(content, proseRun, ceiling))
            pieces.AddRange(CutToCeiling(unit, ceiling));

        proseRun.Clear();
    }

    // The length cascade, weakest structure last. Each level cuts the whole unit on one kind
    // of boundary, packing up to the ceiling as it goes; if any piece is still over, the
    // result is discarded and the next, finer level is tried. The hard cut terminates it - it
    // always fits, which is what makes the fall-through safe to write as a chain.
    private static IReadOnlyList<ContentPiece> CutToCeiling(ContentBlock unit, int ceiling)
    {
        // Fits whole - no cut. The common case, and the one the packer works to produce.
        if (TokenEstimator.Estimate(unit.Text) <= ceiling)
            return [PieceFactory.Whole(unit, BoundaryLevel.None)];

        // Line breaks: the strongest boundary still inside a paragraph.
        var atLines = LineBreakCutter.Cut(unit, ceiling);
        if (CeilingCheck.AllFit(atLines, ceiling)) return atLines;

        // Sentence ends.
        var atSentences = SentenceCutter.Cut(unit, ceiling);
        if (CeilingCheck.AllFit(atSentences, ceiling)) return atSentences;

        // Word gaps - the last boundary the text itself offers.
        var atWords = WordGapCutter.Cut(unit, ceiling);
        if (CeilingCheck.AllFit(atWords, ceiling)) return atWords;

        // Hard cut. Mid-word by construction, and only reached by text that offers no
        // boundary at all - an unbroken token run, a base64 blob, a stripped table.
        return HardCutter.Cut(unit, ceiling);
    }
}
