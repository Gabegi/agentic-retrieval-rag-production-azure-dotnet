using AgenticRagApp.Indexing.Pdf.Utils;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Axis 2 of the two-axis model: cuts one section's text into child pieces.
//
// The two axes compose, they do not multiply. Axis 1 is chosen once per document, axis 2 runs
// per block inside a section - so there is no NxM strategy matrix to implement.
//
// The ceiling is expressed in TOKENS, never characters. Microsoft's 512-token starting point
// is usually quoted as "about 2,000 characters", which assumes 4:1 English; this corpus
// measures 3.1 for Dutch prose and as low as 1.88 for table markdown, so a character ceiling
// means something different for every segment type. The token figure is authoritative and any
// character budget is derived from it.
public interface ITextSplitter
{
    string Name { get; }

    IReadOnlyList<SectionPiece> Split(string sectionText, int tokenCeiling);
}
