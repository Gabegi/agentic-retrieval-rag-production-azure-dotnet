namespace AgenticRagApp.Indexing.Pdf.Models;

// One cut a strategy made, before step 4 turns it into a ChunkObject. Both routes produce
// these and nothing else: a strategy decides WHERE to cut, and knows nothing about ids, page
// attribution or the embedded prefix.
//
// Start/Length are CLEANED-content coordinates - they index into PdfExtractionDocument.Content,
// the same space PageSpan.Offset and LocatedSection use, which is what makes page attribution
// in step 4 possible at all.
//
// THE SLICE INVARIANT, which every cutter is held to:
//
//     content.AsSpan(piece.Start, piece.Length).SequenceEqual(piece.Text)
//
// A piece is a WINDOW onto the source, not a rebuilt string. It fails the moment a cutter
// joins lines back together instead of slicing them, which is exactly how the helpers this
// code replaces lost position (Split('\n') then Join("\n") rewrites \r\n and drops the offset).
//
// Two deliberate exceptions, both flagged on the piece itself:
//   - a table continuation fragment (Degraded aside, BoundaryLevel.TableRow) repeats the header
//     and separator rows, so its Text is composed. Start/Length still address the ROWS it
//     carries - the true position of the data, not of the repeated header.
//   - an overlap piece (IsOverlap) is prefixed with its predecessor's tail. Same rule: the
//     coordinates address this piece's own text.
public sealed record ContentPiece(
    string        Text,
    int           Start,
    int           Length,
    BoundaryLevel BoundaryLevel,
    bool          Degraded  = false,
    bool          IsOverlap = false);

// Which boundary a cut was made on - the fall-through metric for the recursive route.
//
// The order is the ladder itself, weakest structure last: a piece that came back HardCut had no
// usable separator anywhere in it, which almost always means an extraction produced none rather
// than that the text is genuinely unbreakable. Recorded per piece so the run report can count
// how far down the ladder a corpus actually falls.
public enum BoundaryLevel
{
    // No cut was made - the block or section fitted whole. The 83-87% path.
    None,

    // Blank-line boundary. Consumed at parse time on route 2 (BlockParser emits prose blocks
    // already split at blank lines), so a piece is rarely labelled with it.
    Paragraph,

    Line,
    Sentence,
    Word,

    // Mid-word, by construction. Always fits, which is what makes the ladder terminate.
    HardCut,

    // Atomic-kind boundaries: cut between whole rows, or between whole list items.
    TableRow,
    ListItem,
}
