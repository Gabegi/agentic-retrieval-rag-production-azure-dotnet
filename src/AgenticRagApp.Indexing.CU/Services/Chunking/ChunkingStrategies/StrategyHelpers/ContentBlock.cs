namespace AgenticRagApp.Indexing.CU.Services;

// One block of the document, as BlockParser found it: a contiguous slice of the content plus
// what kind of structure that slice shows.
//
// Text is a SLICE of the source, never a rebuild - Start is only meaningful because of that.
// See the slice invariant on ContentPiece.
public sealed record ContentBlock(string Text, int Start, BlockKind Kind)
{
    // Exclusive end, in the same coordinates as Start. Blocks are contiguous, so the next
    // block's Start is this one's End - which is what lets BlockPacker merge a run by slicing
    // from the first block's Start to the last block's End instead of joining their texts.
    public int End => Start + Text.Length;

    // The lines the detectors classify. Blank lines are dropped rather than counted: a trailing
    // blank line inside a run would otherwise make an all-rows table fail its own "every line is
    // a row" test.
    public IReadOnlyList<string> NonBlankLines() =>
        Text.Split('\n')
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();
}

// What a block is, which decides how it may be cut.
//
// Everything except Prose is ATOMIC: cutting it at an arbitrary point destroys information
// rather than merely interrupting it. A row split from its header is a run of numbers with no
// meaning; a value split from its label is unretrievable; half a list item reads as a whole
// one. Only prose degrades gracefully, which is why it is the only kind the length ladder
// (line -> sentence -> word -> hard) is allowed to touch, and the only kind BlockPacker merges.
//
// Diagram (2026-09-22, D214): a fenced block - the machine-readable payload Content
// Understanding writes for a chart or a mermaid figure. Neither typed as a span like a table
// nor detected like a list: DELIMITED, by the fence CU itself writes into the content. Cut
// between whole elements (JSON containers, or lines), never inside one - half a node label
// reads as a whole one, and a flowchart's edges array offers the prose ladder no separator at
// all, which is how one such block reached HardCutter and failed run 260921/1 (D209).
public enum BlockKind
{
    Prose,
    Table,
    KeyValue,
    ListRun,
    Diagram,
}
