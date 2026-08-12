using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Indexing.Pdf.Utils;

namespace AgenticRagApp.Indexing.Pdf.Services;

// The primary branch: cut at validated heading boundaries, then sub-split only the sections
// that need it. Nearly all of this corpus's characters land here.
//
// Two-grain output without two-grain storage: each child carries its section's whole text in
// ParentText, so retrieval can return the section while ranking stayed at the precise child
// grain, with no second round trip ("materialize, don't assemble").
//
// The no-headings case is NOT a separate branch. HeadingLocator returns a single section
// covering the document when it finds nothing, so branch 5 of the cascade falls out of the
// normal path rather than being a route someone has to remember to select - which matters,
// because that branch had almost no test coverage precisely because it was easy to forget.
public sealed class SectionCascadeStrategy : IDocumentChunkingStrategy
{
    private readonly ITextSplitter _splitter;
    private readonly int           _tokenCeiling;

    public string Name => "SectionCascade";

    public SectionCascadeStrategy(ITextSplitter splitter, int tokenCeiling = SectionSplitter.DefaultTokenCeiling)
    {
        _splitter     = splitter;
        _tokenCeiling = tokenCeiling;
    }

    public ChunkingOutcome Chunk(PdfExtractionDocument doc)
    {
        if (string.IsNullOrWhiteSpace(doc.Content))
            return ChunkingOutcome.Empty;

        var located = HeadingLocator.Locate(doc.Content, doc.Headings, doc.PageSpans, doc.Sections);
        var units   = new List<ChunkUnit>();

        foreach (var section in located.Sections)
        {
            var sectionText = doc.Content[section.Start..section.End];
            if (string.IsNullOrWhiteSpace(sectionText)) continue;

            var pieces = _splitter.Split(sectionText, _tokenCeiling);
            if (pieces.Count == 0) continue;

            // ParentText is only worth storing when the section was actually split. On a
            // single-child section the child IS the section, so a copy would be byte-for-byte
            // identical to Content - and Phase A measured 83-87% of sections as never split,
            // so storing it unconditionally would roughly double the corpus's stored text to
            // say nothing at all.
            var parentText = pieces.Count > 1 ? sectionText.Trim() : null;

            var cursor = section.Start;

            for (var i = 0; i < pieces.Count; i++)
            {
                var piece = pieces[i];
                if (string.IsNullOrWhiteSpace(piece.Text)) continue;

                // Where this piece sits in the document, tracked forward rather than searched
                // for: with overlap, two consecutive children share text, so an IndexOf from
                // the section start would keep resolving to the earlier copy and every page
                // attribution after the first overlap would be wrong.
                var at = doc.Content.IndexOf(piece.Text, cursor, StringComparison.Ordinal);
                if (at < 0) at = cursor;
                cursor = Math.Min(at + 1, Math.Max(section.Start, section.End - 1));

                units.Add(new ChunkUnit(
                    Grain:          ChunkGrain.Child,
                    SectionIndex:   section.Index,
                    ChildIndex:     i,
                    Content:        piece.Text,
                    ParentText:     parentText,
                    HeadingText:    section.HeadingText,
                    HeadingPath:    section.HeadingPath,
                    HeadingDepth:   section.Depth,
                    HeadingSource:  section.HeadingSource,
                    HeadingLocated: section.Located,
                    IsOverlap:      piece.IsOverlap,
                    Start:          at,
                    Length:         piece.Text.Length));
            }
        }

        return new ChunkingOutcome(
            units,
            located.HeadingsTotal,
            located.HeadingsLocated,
            located.PairedHeadingsMerged);
    }
}
